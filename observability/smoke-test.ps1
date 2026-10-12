#Requires -Version 5.1
# Checks the whole telemetry path: API -> Collector -> Prometheus, Tempo and Loki.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File observability\smoke-test.ps1

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$apiProject = "src\UmutLedger"
$apiPort = 5289
$apiUrl = "http://localhost:$apiPort"
$prometheusUrl = "http://localhost:9090"
$grafanaUrl = "http://localhost:3000"
$timeoutSeconds = 120
$pollSeconds = 3
$movementRounds = 6
$secondsBetweenRounds = 6
$counterQueries = @(
    'sum(ledger_movements_total{outcome="created"})',
    'sum(ledger_movements_total{outcome="replayed"})',
    'sum(ledger_movements_total{outcome="insufficient_funds"})',
    'sum(ledger_movements_amount_total{currency="USD"})',
    'sum(ledger_account_lock_wait_duration_seconds_count)'
)

function Wait-Until([string]$description, [scriptblock]$condition) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            if (& $condition) {
                Write-Host "ok       $description"
                return
            }
        }
        catch { }
        Start-Sleep -Seconds $pollSeconds
    }
    throw "MISSING: $description (waited $timeoutSeconds seconds)"
}

function Invoke-Native([string]$description, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "FAILED: $description"
    }
}

function Test-Url([string]$url) {
    (Invoke-WebRequest -Uri $url -UseBasicParsing).StatusCode -eq 200
}

function Get-PrometheusTotal([string]$query) {
    $response = Invoke-RestMethod -Uri "$prometheusUrl/api/v1/query" -Body @{ query = $query }
    $series = @($response.data.result)
    if ($series.Count -eq 0) {
        return [double]0
    }
    return [double]$series[0].value[1]
}

function Test-LokiHas([string]$query) {
    $lokiUrl = "$grafanaUrl/api/datasources/proxy/uid/loki/loki/api/v1/query_range"
    $response = Invoke-RestMethod -Uri $lokiUrl -Body @{ query = $query }
    @($response.data.result).Count -gt 0
}

function Send-Json([string]$path, [hashtable]$body, [hashtable]$headers = @{}) {
    try {
        $response = Invoke-WebRequest -Method Post -Uri "$apiUrl$path" -Body ($body | ConvertTo-Json -Compress) `
            -ContentType "application/json" -Headers $headers -UseBasicParsing
        return @{ Status = [int]$response.StatusCode; Body = ($response.Content | ConvertFrom-Json) }
    }
    catch {
        if ($null -eq $_.Exception.Response) {
            throw
        }
        return @{ Status = [int]$_.Exception.Response.StatusCode; Body = $null }
    }
}

function Assert-Status($response, [int]$expected, [string]$description) {
    if ($response.Status -ne $expected) {
        throw "FAILED: $description returned $($response.Status), expected $expected"
    }
    Write-Host "ok       $description -> $expected"
}

Set-Location $repositoryRoot

if (Get-NetTCPConnection -LocalPort $apiPort -State Listen -ErrorAction SilentlyContinue) {
    throw "Port $apiPort is already in use. Stop the running API and run the smoke test again."
}

Invoke-Native "docker compose up" { docker compose up -d }
Wait-Until "PostgreSQL accepts connections" {
    docker compose exec -T postgres pg_isready -U umutledger | Out-Null
    $LASTEXITCODE -eq 0
}
Wait-Until "Prometheus is ready" { Test-Url "$prometheusUrl/-/ready" }
Wait-Until "Grafana is ready" { Test-Url "$grafanaUrl/api/health" }

Invoke-Native "dotnet tool restore" { dotnet tool restore }
Invoke-Native "database migration" { dotnet ef database update --project $apiProject }
Invoke-Native "build" { dotnet build $apiProject --nologo --verbosity quiet }

$totalsBefore = @{}
foreach ($query in $counterQueries) {
    $totalsBefore[$query] = Get-PrometheusTotal $query
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = $apiUrl
$apiProcess = Start-Process -FilePath "dotnet" -ArgumentList "bin\Debug\net10.0\UmutLedger.dll" `
    -WorkingDirectory (Join-Path $repositoryRoot $apiProject) -PassThru -WindowStyle Hidden

try {
    Wait-Until "API is listening" { Test-Url "$apiUrl/openapi/v1.json" }

    $run = "smoke-" + [guid]::NewGuid().ToString("N")
    $traceId = [guid]::NewGuid().ToString("N")
    $spanId = [guid]::NewGuid().ToString("N").Substring(0, 16)
    $traceParent = "00-$traceId-$spanId-01"

    $alice = Send-Json "/accounts" @{ name = "Smoke Alice"; currency = "USD" }
    Assert-Status $alice 201 "create account Alice"
    $bob = Send-Json "/accounts" @{ name = "Smoke Bob"; currency = "USD" }
    Assert-Status $bob 201 "create account Bob"

    $transferBody = @{ debitAccountId = $alice.Body.id; creditAccountId = $bob.Body.id; amount = 2500; currency = "USD" }
    for ($round = 1; $round -le $movementRounds; $round++) {
        if ($round -gt 1) {
            Start-Sleep -Seconds $secondsBetweenRounds
        }
        $depositHeaders = @{ "Idempotency-Key" = "$run-deposit-$round" }
        if ($round -eq 1) {
            $depositHeaders["traceparent"] = $traceParent
        }
        $deposit = Send-Json "/accounts/$($alice.Body.id)/deposits" @{ amount = 10000; currency = "USD" } $depositHeaders
        Assert-Status $deposit 201 "round $round deposit (trace $traceId)"

        $transferKey = @{ "Idempotency-Key" = "$run-transfer-$round" }
        Assert-Status (Send-Json "/transfers" $transferBody $transferKey) 201 "round $round transfer"
        Assert-Status (Send-Json "/transfers" $transferBody $transferKey) 200 "round $round replayed transfer"

        $overdraft = Send-Json "/accounts/$($bob.Body.id)/withdrawals" @{ amount = 999999; currency = "USD" } `
            @{ "Idempotency-Key" = "$run-overdraft-$round" }
        Assert-Status $overdraft 422 "round $round overdraft withdrawal"
    }

    foreach ($query in $counterQueries) {
        Wait-Until "Prometheus increased $query" { (Get-PrometheusTotal $query) -gt $totalsBefore[$query] }
    }
    Wait-Until "Tempo has trace $traceId" { Test-Url "$grafanaUrl/api/datasources/proxy/uid/tempo/api/traces/$traceId" }
    Wait-Until "Loki has the log line of trace $traceId" {
        Test-LokiHas "{service_name=`"umutledger`"} | trace_id=`"$traceId`""
    }
}
finally {
    Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "SMOKE TEST PASSED. The containers are still running: open $grafanaUrl and look at the 'Ledger overview' dashboard."
