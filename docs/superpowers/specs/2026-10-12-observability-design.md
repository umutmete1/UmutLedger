# Observability Design

Date: 2026-10-12
Status: awaiting review

## 1. Purpose and scope

The core ledger works and is tested, but while it runs the only thing it tells you is what it prints to the console. This sub-project makes the running ledger visible: how many requests it serves, how fast, what goes wrong, how money movements end, and where requests wait.

It is the second sub-project, after the core ledger. Its numbers are also what the later load-testing sub-project will read.

This spec covers:

- Metrics, traces and logs emitted by the API through OpenTelemetry
- Three ledger-specific metrics: movement outcomes, money volume and account-lock wait time
- A local monitoring stack in `docker-compose.yml`: OpenTelemetry Collector, Prometheus, Tempo, Loki and Grafana
- One Grafana dashboard, loaded from a file
- Tests at 100% coverage for the new API code, and a smoke test for the stack

Out of scope:

- Running the API in a container, and Kubernetes manifests for the API or this stack (sub-project 3)
- Load tests (sub-project 4) and authentication
- Alert rules and notifications
- Health check endpoints (they arrive with the Kubernetes probes)
- Exemplars (jumping from a point on a graph to one matching trace)
- PostgreSQL's own server metrics through a separate exporter
- Long-term retention; local data uses each store's default retention

The API keeps running on the developer's machine with `dotnet run`. Only the monitoring services run in containers.

## 2. Decisions

| Topic | Decision |
|---|---|
| Signals | Metrics, traces and logs, all viewable in Grafana |
| Stores | Prometheus for metrics, Grafana Tempo for traces, Grafana Loki for logs |
| Pipeline | The API sends OTLP to an OpenTelemetry Collector, which forwards each signal to its store |
| Ledger metrics | Movement outcomes, money volume and account-lock wait time |
| Where outcomes are recorded | A decorator around `ITransferService` |
| Where lock time is recorded | Inside `TransferService`, around the existing lock query |
| No endpoint configured | The API exports nothing and behaves as it does today |
| Sampling | None. Every request is traced |
| Stack verification | A smoke-test script run by hand, not part of `dotnet test` |

### Why a Collector

Two alternatives were considered and rejected.

- **The API talks to each store directly** (Prometheus scrapes `/metrics`, traces and logs go straight to Tempo and Loki). One container fewer, but store addresses would live in the API's configuration, the Prometheus exporter package for .NET is a prerelease, and the wiring would be redone for Kubernetes.
- **The all-in-one `grafana/otel-lgtm` image.** Fastest to start, but it is meant for demos, hides the configuration that Kubernetes will need, and would be replaced entirely in sub-project 3.

With a Collector the API knows one endpoint and no store names. Changing a store or moving to Kubernetes changes Collector configuration only.

## 3. Architecture and data flow

```
 UmutLedger API (dotnet run, on the developer's machine)
        |  OTLP over gRPC, localhost:4317
        v
 OpenTelemetry Collector --> Prometheus  (metrics)
        |                --> Tempo       (traces)
        |                --> Loki        (logs)
        v
                    Grafana (localhost:3000) reads all three
```

- The OpenTelemetry SDK is registered at startup. It exports metrics, traces and logs over OTLP to a single endpoint.
- The endpoint is read from the configuration key `OTEL_EXPORTER_OTLP_ENDPOINT`. `appsettings.Development.json` sets it to `http://localhost:4317`. `appsettings.json` does not set it.
- `appsettings.Development.json` also sets `OTEL_METRIC_EXPORT_INTERVAL` to `5000`, so metrics reach the dashboard every 5 seconds locally. The SDK default is 60 seconds.
- When the key is absent or empty, no exporter is registered. This is what keeps the tests, and any environment without a Collector, working unchanged.
- When the key is set but nothing is listening, the exporter drops telemetry quietly. A missing Collector never fails or slows a request.
- Console logging stays, so `dotnet run` output is the same as today.
- The service identifies itself as `service.name = umutledger`, with the assembly version as `service.version`.

### What links the three signals

Every request gets an OpenTelemetry trace id. Its log lines carry that id, and the `traceId` field of an error response becomes that same id. From a failed request, the `traceId` leads to the trace in Tempo and the log lines in Loki.

## 4. What the API emits

### 4.1 Built-in telemetry

Switched on by registration, with no code of ours:

| Source | Metrics | Traces |
|---|---|---|
| ASP.NET Core | Request count and duration per route and status code | One span per request |
| Npgsql | Connection pool usage and command duration | One span per SQL command |
| .NET runtime | Garbage collection, heap and thread pool | none |

SQL text is recorded on database spans. Parameter values are not.

### 4.2 Ledger metrics

All three come from one meter named `UmutLedger`.

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `ledger.movements` | counter | `kind`, `currency`, `outcome` | One per deposit, withdrawal or transfer request that reached the transfer service |
| `ledger.movements.amount` | counter, minor units | `kind`, `currency` | Money moved. Added only when a transfer is newly created |
| `ledger.account_lock.wait.duration` | histogram, seconds | `kind`, `currency` | Time taken to acquire the account row locks |

Label values:

- `kind`: `deposit`, `withdrawal` or `transfer`.
- `currency`: `USD`, `EUR` or `GBP`.
- `outcome`: one of
  - `created`: a new transfer was written
  - `replayed`: an existing transfer was returned for a repeated idempotency key
  - a ledger error code, for example `insufficient_funds`, `idempotency_key_reused`, `currency_mismatch`, `account_not_found`, `system_account_not_allowed`, `same_account_transfer`, `validation_failed`
  - `cancelled`: the request was cancelled
  - `error`: any other exception

Rules:

- Account ids, account names and idempotency keys are never labels. The set of series is small and fixed.
- `ledger.movements.amount` is never increased for a replay, a rejection, a cancellation or an error. Amounts of different currencies are never added together.
- `ledger.account_lock.wait.duration` is recorded once for each request that runs the lock query. A request answered as a replay before locking records nothing.
- The lock wait covers the lock query's whole round trip, so it includes network time to the database as well as time spent queued behind another transaction.

**Known limit.** `ledger.movements` only counts requests that reach the transfer service. A request rejected earlier (missing or over-long `Idempotency-Key` header, unknown currency, malformed JSON) is counted by the built-in HTTP metrics as a `400` on its route and does not appear in `ledger.movements`. A zero or negative amount is not in that group: it is refused inside the transfer service and counted as `validation_failed`.

**Two more limits.** A lock wait is recorded only when the lock query returns, so a wait that ends in a cancellation, a timeout or a lost connection is missing from the histogram, which understates the worst waits. And if a request is cancelled or fails while its commit is in flight and the commit still lands, the movement is counted as `cancelled` or `error` with no amount, and the client's retry is a replay; `ledger.movements.amount` then under-counts that one transfer.

### 4.3 Trace attributes

The request span of a deposit, withdrawal or transfer gets three attributes:

| Attribute | Value |
|---|---|
| `ledger.movement.kind` | Same values as the `kind` label |
| `ledger.movement.outcome` | Same values as the `outcome` label |
| `ledger.transfer.id` | The transfer id, when the outcome is `created` or `replayed` |

No hand-written spans are added. The lock query already appears as its own database span.

### 4.4 Logs

Everything the API already writes through `ILogger` is exported with the trace id attached.

One new log line is written per money movement, at `Information` level, for every outcome. Fields: kind, outcome, amount, currency, the account ids named in the request (one text field, ids separated by a comma and a space), and the transfer id when there is one.

The idempotency key is not logged.

### 4.5 Error responses

`traceId` in a problem details body becomes the OpenTelemetry trace id of the request. If a request has no active trace, it falls back to ASP.NET's request identifier, which is today's value.

## 5. Code structure

| Unit | Layer | Responsibility |
|---|---|---|
| `MovementKind` | Application | The three kinds of money movement |
| `ILedgerMetrics` | Application | Records a movement outcome, a moved amount and a lock wait. The only thing application code knows about metrics |
| `LedgerMetrics` | Infrastructure | Implements `ILedgerMetrics` with the `UmutLedger` meter. Owns the metric names and label names |
| `InstrumentedTransferService` | Application | Decorates `ITransferService`. Calls the real service, then records the outcome, the amount, the span attributes and the log line |
| `TransferService` | Application | Unchanged except for timing the lock query and reporting it to `ILedgerMetrics` |
| `LedgerProblem` | Api | Reads the trace id from the current trace, with the fallback |
| Telemetry registration | Api | One extension method called from `Program.cs` that registers the SDK and, when an endpoint is configured, the OTLP exporter |

- `InstrumentedTransferService` returns the real service's result and rethrows its exceptions unchanged. It never swallows or replaces an exception of the real service.
- A failure inside telemetry itself (a metric, a span tag or a log call that throws) is contained and reported as a warning. It never reaches the caller, so it cannot fail a transfer or replace a ledger error.
- `TransferService` measures the lock wait with the `TimeProvider` it already receives. The timing adds no query and does not change the transaction.
- Application code depends on `ILedgerMetrics`, not on OpenTelemetry types.
- The project rules apply as before: self-validating classes, no magic numbers (metric names, label names and outcome values are named constants), small functions, no duplication.

Packages added to the API: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore` and `Npgsql.OpenTelemetry`. Versions are the current stable releases at implementation time.

## 6. The monitoring stack

### 6.1 Services

Added to `docker-compose.yml` beside `postgres`:

| Service | Role | Published port | Data volume |
|---|---|---|---|
| `otel-collector` | Receives OTLP from the API, batches it, forwards each signal | `4317` | no |
| `prometheus` | Stores metrics | `9090` | yes |
| `tempo` | Stores traces | none | yes |
| `loki` | Stores logs | none | yes |
| `grafana` | Dashboards and search over all three | `3000` | no |

- Image versions are pinned to the current stable releases at implementation time. No `latest` tags.
- `docker compose up -d` starts everything. `docker compose up -d postgres` starts only the database.
- Tempo and Loki are reached only through Grafana and publish no ports.

### Where the data is stored

| Signal | Stored by | On disk in the Docker volume |
|---|---|---|
| Logs | Loki | `umutledger-loki` |
| Traces | Tempo | `umutledger-tempo` |
| Metrics | Prometheus | `umutledger-prometheus` |

- Each store writes to the local filesystem inside its own named volume, the same way PostgreSQL uses `umutledger-postgres`. No cloud or object storage is involved.
- The data survives `docker compose down` and is deleted by `docker compose down -v`.
- Logs are also still printed to the console of the running API, as today. The console copy is not kept anywhere.
- The Collector and Grafana store nothing: the Collector only passes data on, and Grafana only reads.

### 6.2 Collector pipeline

- One OTLP receiver.
- Two processors: a memory limiter and batching. No sampling and no filtering.
- Metrics are pushed to Prometheus's OTLP receiver, traces to Tempo over OTLP, logs to Loki over OTLP. Prometheus scrapes nothing.

Prometheus rewrites metric names. The expected forms are `ledger_movements_total`, `ledger_movements_amount_total` and `ledger_account_lock_wait_duration_seconds`. The smoke test confirms the exact names, and the dashboard uses whatever it confirms.

### 6.3 Files

```
observability/
  otel-collector/config.yaml
  prometheus/prometheus.yml
  tempo/tempo.yaml
  loki/loki.yaml
  grafana/provisioning/datasources/datasources.yaml
  grafana/provisioning/dashboards/dashboards.yaml
  grafana/dashboards/ledger-overview.json
  smoke-test.ps1
```

### 6.4 Grafana

- Opens at `http://localhost:3000` with no login. This is for local use only and must not be carried into a shared environment.
- The Prometheus, Tempo and Loki data sources are created from the provisioning file at startup.
- The data sources are linked: a trace leads to its log lines, and a log line with a trace id leads to its trace.
- The dashboard file is the source of truth. A change made in the Grafana UI is lost on restart unless it is exported back into the file.

### 6.5 The "Ledger overview" dashboard

| Row | Panels |
|---|---|
| Traffic | Requests per second by route; error rate split into 4xx and 5xx; latency p50, p95 and p99 by route |
| Money movements | Movements per second by kind and outcome; share of movements that were replays; rejections by error code; money moved per currency |
| Locking | Lock wait p50, p95 and p99 by kind and currency |
| Database and runtime | PostgreSQL connections in use; SQL command duration p95; heap size and garbage collections |
| Logs | Latest warning and error log lines |

The Locking row is where the known trade-off from the core ledger shows: deposits and withdrawals in one currency all queue on that currency's world account.

## 7. Testing

### 7.1 Rules

- 100% line and branch coverage on the API, enforced by `dotnet test -p:CollectCoverage=true` as today.
- Real PostgreSQL through Testcontainers. No mocked database.
- The tests need no Collector, Prometheus, Tempo, Loki or Grafana.

### 7.2 Existing tests

`InstrumentedTransferService` is registered in the real application, so all existing tests run through it. Any change it made to a result, a status code or an exception would fail them.

### 7.3 New tests

Tests listen to the application's real meter, trace source and logger in-process and assert what was recorded.

| Area | What is asserted |
|---|---|
| Outcomes | One test per outcome: `created`, `replayed`, each ledger error code that can reach the transfer service, `cancelled` and `error`, each with the right `kind` and `currency` |
| Volume | The amount is added once for a created movement, and not at all for a replay, a rejection, a cancellation or an error |
| Volume under concurrency | Two identical requests sent at once add the amount exactly once |
| Lock timing | A measurement is recorded for a movement that runs the lock query, and none for a replay found before locking |
| Lock timing value | With an account's row lock held by a second connection for a set time, the recorded wait is at least that long |
| Trace id | A request sent with a known `traceparent` header gets an error response whose `traceId` is that id |
| Trace id fallback | With no active trace, `traceId` is the request identifier |
| Span attributes | The request span carries kind, outcome and, when there is one, the transfer id |
| Log line | Each movement writes its line with the expected fields, and the idempotency key appears nowhere in it |
| Configuration | The OTLP exporter is registered when the endpoint is configured and absent when it is not |
| Decorator | A result is returned unchanged and an exception is rethrown unchanged |

The test project adds `Microsoft.Extensions.Diagnostics.Testing` for collecting metrics and log records.

### 7.4 Smoke test for the stack

Configuration files and the dashboard are outside the coverage gate. `observability/smoke-test.ps1` checks them end to end. It is run by hand.

1. Starts the compose stack and the API, and waits until both answer.
2. Sends a few movements, including one replay and one rejection.
3. Asks Prometheus for each of the three ledger metrics.
4. Asks Tempo, through Grafana, for the trace of one of the requests.
5. Asks Loki, through Grafana, for that request's log line.
6. Stops the API it started and reports pass or fail, naming whatever was missing.

The script leaves the containers running so the dashboard can be looked at. How the dashboard looks is checked by eye.

## 8. Documentation

- `README.md`: the run instructions gain the monitoring stack, the Grafana address and how to find a trace from an error's `traceId`. The "Not in this version" list loses observability.
- `CLAUDE.md`: the run-and-test tutorial gains a step for Grafana and a step for the smoke test.

## 9. Definition of done

- All tests pass with 100% line and branch coverage.
- The smoke test passes on the developer's machine.
- Every panel of the dashboard shows data after the smoke test.
- `README.md` and the `CLAUDE.md` tutorial describe the new steps.
