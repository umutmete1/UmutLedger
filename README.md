# UmutLedger

A double-entry banking ledger Web API that moves artificial funds between accounts. Built on .NET 10 and PostgreSQL.

## What it does

- Customer accounts in USD, EUR or GBP.
- Deposits, withdrawals and transfers between accounts of the same currency.
- Every money movement is idempotent: send an `Idempotency-Key` header, and a retried request returns the original result instead of applying twice.
- Every transfer writes two immutable entries that sum to zero. Balances can always be re-derived from the entries.

## Run it locally

Docker Desktop must be running.

```powershell
docker compose up -d
dotnet tool restore
dotnet ef database update --project src/UmutLedger
dotnet run --project src/UmutLedger
```

The API listens on `http://localhost:5289`. Amounts are in minor units, so `10000` is 100.00.

`docker compose up -d` also starts the monitoring stack: an OpenTelemetry Collector, Prometheus, Tempo, Loki and Grafana. To start only the database, run `docker compose up -d postgres`; the API then runs as before and drops its telemetry.

To try the endpoints, open the API reference at `http://localhost:5289/scalar`. It is built from the app's OpenAPI document (`/openapi/v1.json`), lists every endpoint and sends requests from the browser. It is only served in the Development environment. Sample requests are also in `src/UmutLedger/UmutLedger.http`.

## See what it is doing

Open Grafana at `http://localhost:3000` (no login, local use only) and choose the "Ledger overview" dashboard. It shows request rates and latency, money movements by kind and outcome, replays, rejections, money moved per currency, how long movements wait for account locks, and the latest warnings and errors.

Every error response carries a `traceId`. In Grafana, choose Explore, then the Tempo data source, and paste that id to see the request's trace; the trace links to its log lines in Loki.

To check the whole path from the API to Grafana, stop the API and run:

```powershell
powershell -ExecutionPolicy Bypass -File observability\smoke-test.ps1
```

| Signal | Stored by | Docker volume |
|---|---|---|
| Metrics | Prometheus (`http://localhost:9090`) | `umutledger-prometheus` |
| Traces | Tempo | `umutledger-tempo` |
| Logs | Loki | `umutledger-loki` |

The API sends telemetry to the address in `OTEL_EXPORTER_OTLP_ENDPOINT`, which `appsettings.Development.json` sets to the Collector at `http://localhost:4317`. Where that setting is absent, nothing is exported.

## Run the tests

Docker Desktop must be running. The tests start their own PostgreSQL container.

```powershell
dotnet test -p:CollectCoverage=true
```

The run fails if line or branch coverage is below 100%.

## Build the Docker image

```powershell
docker build -t umutledger .
```

The image runs the API as a non-root user on port 8080. It holds no connection string, so pass one in `ConnectionStrings__Ledger`. To run it against the database from `docker compose up -d postgres`:

```powershell
docker run --rm -p 8080:8080 --network umutledger_default `
    -e "ConnectionStrings__Ledger=Host=postgres;Port=5432;Database=umutledger;Username=umutledger;Password=umutledger" `
    umutledger
```

The API then answers on `http://localhost:8080`. The container runs in the Production environment, so the API reference at `/scalar` is not served and no telemetry is exported unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

## Endpoints

| Method and path | Purpose |
|---|---|
| `POST /accounts` | Create a customer account |
| `GET /accounts/{id}` | Account details and balance |
| `GET /accounts/{id}/entries` | Entry history, newest first, paged with `limit` and `cursor` |
| `POST /accounts/{id}/deposits` | Move money into an account |
| `POST /accounts/{id}/withdrawals` | Move money out of an account |
| `POST /transfers` | Move money between two customer accounts |
| `GET /transfers/{id}` | A transfer and its two entries |

Errors are RFC 9457 problem details with a stable `code`, for example `insufficient_funds` or `idempotency_key_reused`. Framework errors use the same shape: an unknown route is `not_found` (404), a wrong method is `method_not_allowed` (405) and a non-JSON body is `unsupported_media_type` (415).

## Design notes

**Double-entry with world accounts.** Money cannot appear or vanish. Each currency has one system "world" account that stands for everything outside the ledger. A deposit debits the world account and credits the customer; a withdrawal does the reverse. The world account is the only account allowed to go negative.

**Correctness under concurrency.** A transfer runs in one database transaction. It locks both account rows with `SELECT ... FOR UPDATE` in ascending id order, so opposing transfers queue instead of deadlocking. Two identical requests with the same key need the same account rows, so the second waits for the first's locks. After taking them it looks the key up again, finds the committed transfer and returns it as a replay (200), even if the first request left too little money to run again. Two different requests with the same key that share no account do not wait on each other's row locks, so the unique constraint on the key lets only one commit. The other is rejected with `idempotency_key_reused`.

**The database is the last line of defence.** `CHECK` constraints refuse a negative customer balance or a zero amount, and a trigger makes transfers and entries append-only. A mistake is corrected by a new reversing transfer, never by an edit.

**No full Clean Architecture, on purpose.** The service has a handful of operations. It keeps what pays for itself: a pure, self-validating domain, thin controllers, and services behind interfaces. It leaves out repository interfaces and a project per layer, because the tests use real PostgreSQL (so there is nothing to swap for a fake) and the row locks and constraints are part of the design rather than a detail to hide.

**Telemetry stays out of the money path.** A decorator around the transfer service records each movement's outcome, amount and log line after the real service has returned or thrown, and passes the result or the exception through untouched. The transfer service itself only measures how long its lock query took. The outcome counter sees requests that reach the transfer service; a request refused earlier (missing idempotency key, unknown currency, malformed JSON) shows up as a `400` in the HTTP metrics instead.

**Database connections are capped per instance.** A transfer waiting for a row lock still holds its connection, so many callers on one busy account can occupy many connections while doing no work. The API therefore limits its pool to 50 connections unless the connection string sets `Maximum Pool Size` itself, and requests beyond that wait inside the API. PostgreSQL's `max_connections` is 100 (set in `docker-compose.yml`). The rule to keep: number of API instances × pool size must stay below `max_connections`, with room left for other clients. With the driver's default of 100, a test with 150 callers on two accounts had 5,673 connection attempts refused in 30 seconds; with 50 it had none.

**Known trade-off.** Deposits and withdrawals in one currency all lock that currency's world account, so they run one at a time. Transfers between customers are not affected.

## Not in this version

Authentication, Kubernetes deployment, and load tests are planned as separate pieces of work.
