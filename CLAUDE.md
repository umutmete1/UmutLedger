## ABOUT THE PROJECT
This is a banking ledger web api project that will enable the transfer of artificial funds from one account to another

## STACK
API => .NET 10 
DATABASE => PostgreSQL
TESTING => xUnit,Testcontainers (real Postgres in tests, no mocks for the DB), NBomber or k6 for load tests
LOGING => OpenTelemetry, Prometheus, Grafana
CONTAINER => Docker
SCALABILITY => Kubernetes

## RULES
- Even if its an artificial money transfer there is no room for error in money transfer functionalities 
- Transactions must be idompotent
- Test coverage must be 100%
- All code must follow clean code principles: intention-revealing names, small functions that do one thing, no duplication, no dead code, no magic numbers, and comments only where the code cannot explain itself
- All code must follow SOLID principles: Single Responsibility, Open/Closed, Liskov Substitution, Interface Segregation, Dependency Inversion
- Every class must be self-validating: it checks its own invariants on creation and on every state change, so an instance can never exist in an invalid state. No public setters or parameterless public constructors that allow a faulty object to be built

## HOW TO RUN AND TEST THE APP
All commands are PowerShell, run from the repository root (`C:\UmutLedger`).

### 1. Prerequisites
- .NET 10 SDK (`dotnet --version` prints 10.x)
- Docker Desktop, started and running (`docker ps` works without an error)

### 2. Start the database and the monitoring stack
```powershell
docker compose up -d
```
This starts PostgreSQL 17 on `localhost:5432` with database, user and password all `umutledger`, and the monitoring stack (OpenTelemetry Collector, Prometheus, Tempo, Loki, Grafana). The data is kept in Docker volumes, so it survives a restart. To start only the database, run `docker compose up -d postgres`.

### 3. Create the tables
```powershell
dotnet tool restore
dotnet ef database update --project src/UmutLedger
```
Needed on the first run and after every new migration. It creates the `accounts`, `transfers` and `entries` tables and the three system "world" accounts (World USD, World EUR, World GBP).

### 4. Start the API
```powershell
dotnet run --project src/UmutLedger
```
The API listens on `http://localhost:5289`. Leave this terminal open and use a second one for the next steps. Stop the API with `Ctrl+C`.

### 5. Open the API reference
Browse to `http://localhost:5289/scalar`. It lists every endpoint and sends requests from the browser. It is only served in the Development environment, which `dotnet run` uses by default.

Things to know before sending requests:
- Amounts are in minor units: `10000` means 100.00.
- Currencies are `USD`, `EUR` or `GBP`.
- Deposits, withdrawals and transfers need an `Idempotency-Key` header (any text up to 255 characters).

### 6. Walk through a full scenario
Run these in a second PowerShell window. The same requests can be sent from Scalar or from `src/UmutLedger/UmutLedger.http` instead.

```powershell
$api = "http://localhost:5289"
$json = "application/json"

# Create two accounts
$alice = Invoke-RestMethod -Method Post "$api/accounts" -ContentType $json -Body '{ "name": "Alice", "currency": "USD" }'
$bob   = Invoke-RestMethod -Method Post "$api/accounts" -ContentType $json -Body '{ "name": "Bob", "currency": "USD" }'

# Deposit 100.00 USD into Alice
Invoke-RestMethod -Method Post "$api/accounts/$($alice.id)/deposits" -ContentType $json `
    -Headers @{ "Idempotency-Key" = "deposit-0001" } -Body '{ "amount": 10000, "currency": "USD" }'

# Transfer 25.00 USD from Alice to Bob
$transferBody = "{ `"debitAccountId`": `"$($alice.id)`", `"creditAccountId`": `"$($bob.id)`", `"amount`": 2500, `"currency`": `"USD`" }"
$transfer = Invoke-RestMethod -Method Post "$api/transfers" -ContentType $json `
    -Headers @{ "Idempotency-Key" = "transfer-0001" } -Body $transferBody

# Withdraw 10.00 USD from Bob
Invoke-RestMethod -Method Post "$api/accounts/$($bob.id)/withdrawals" -ContentType $json `
    -Headers @{ "Idempotency-Key" = "withdrawal-0001" } -Body '{ "amount": 1000, "currency": "USD" }'

# Check the results
Invoke-RestMethod "$api/accounts/$($alice.id)"          # balance 7500
Invoke-RestMethod "$api/accounts/$($bob.id)"            # balance 1500
Invoke-RestMethod "$api/accounts/$($alice.id)/entries"  # entry history, newest first
Invoke-RestMethod "$api/transfers/$($transfer.id)"      # the transfer and its two entries
```

Expected: Alice ends with `7500` and Bob with `1500`.

### 7. Check idempotency
Send the transfer again with the same key and the same body:
```powershell
$replay = Invoke-RestMethod -Method Post "$api/transfers" -ContentType $json `
    -Headers @{ "Idempotency-Key" = "transfer-0001" } -Body $transferBody
$replay.id -eq $transfer.id   # True
```
The first call answered `201 Created`; the replay answers `200 OK` with the same transfer, and the balances do not change (Alice is still `7500`).

Reusing the key `transfer-0001` with a different body (for example amount `9999`) must fail with `422` and code `idempotency_key_reused`.

### 8. Check the error cases
`Invoke-RestMethod` throws on these, which is the expected result. Errors are problem details with a stable `code`.

| Request | Status | `code` |
|---|---|---|
| Deposit without the `Idempotency-Key` header | 400 | `idempotency_key_required` |
| Amount `0` or negative, or an unknown currency | 400 | `validation_failed` |
| Transfer with the same debit and credit account | 400 | `same_account_transfer` |
| Account or transfer id that does not exist | 404 | `account_not_found` / `transfer_not_found` |
| Withdraw or transfer more than the balance | 422 | `insufficient_funds` |
| Request currency differs from the account currency | 422 | `currency_mismatch` |
| Transfer that uses a world account id | 422 | `system_account_not_allowed` |

### 9. Check the database directly
Connect with DataGrip (or any client) to host `localhost`, port `5432`, database `umutledger`, user `umutledger`, password `umutledger`. Or open `psql` in the container:
```powershell
docker compose exec postgres psql -U umutledger -d umutledger
```
Useful checks:
```sql
-- Every transfer's entries must sum to zero (this must return no rows)
SELECT transfer_id, SUM(amount) FROM entries GROUP BY transfer_id HAVING SUM(amount) <> 0;

-- Every stored balance must equal the sum of its entries (this must return no rows)
SELECT a.id, a.name, a.balance, COALESCE(SUM(e.amount), 0) AS derived
FROM accounts a LEFT JOIN entries e ON e.account_id = a.id
GROUP BY a.id HAVING a.balance <> COALESCE(SUM(e.amount), 0);

-- All money in the ledger, world accounts included, must sum to zero per currency
SELECT currency, SUM(balance) FROM accounts GROUP BY currency;

-- Entries are append-only: this must be rejected by the trigger
UPDATE entries SET amount = 1;
```

### 10. Run the automated tests
Docker Desktop must be running. The tests start their own PostgreSQL container, so they do not touch the database from step 2.
```powershell
dotnet test -p:CollectCoverage=true
```
The run fails if a test fails or if line or branch coverage is below 100%. Without `-p:CollectCoverage=true` the tests still run, but coverage is not checked.

### 11. See the telemetry in Grafana
With the containers and the API running, open `http://localhost:3000` (no login) and choose the "Ledger overview" dashboard. Send a few requests (step 6) and the panels fill within about 10 seconds.

To follow one request: every error response has a `traceId`. In Grafana choose Explore, the Tempo data source, and paste the id. The trace shows the request, its SQL commands, and a link to its log lines in Loki.

Prometheus is also reachable directly at `http://localhost:9090`.

### 12. Run the smoke test for the monitoring stack
Stop the API first (the script starts its own copy on port 5289).
```powershell
powershell -ExecutionPolicy Bypass -File observability\smoke-test.ps1
```
It starts the containers and the API, sends a deposit, a transfer, a replay and a rejected withdrawal in several rounds, then checks that the metrics reached Prometheus, the trace reached Tempo and the log line reached Loki. It ends with `SMOKE TEST PASSED`, or with a `MISSING:` or `FAILED:` line naming what did not arrive.

### 13. Stop and clean up
```powershell
docker compose down      # stop the containers, keep the data
docker compose down -v   # stop the containers and delete all data
```
After `down -v`, repeat steps 2 and 3 to start from an empty ledger.

