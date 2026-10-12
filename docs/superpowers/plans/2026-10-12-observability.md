# Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the running ledger visible: the API emits metrics, traces and logs through OpenTelemetry, and a local Collector, Prometheus, Tempo, Loki and Grafana stack stores and shows them.

**Architecture:** Three ledger metrics sit behind one small interface, `ILedgerMetrics`. A decorator around `ITransferService` records movement outcomes, money volume, span attributes and one log line; `TransferService` itself only gains timing around its lock query. One registration method wires the OpenTelemetry SDK and, only when an endpoint is configured, an OTLP exporter that sends everything to a Collector, which forwards to Prometheus, Tempo and Loki.

**Tech Stack:** .NET 10 (SDK 10.0.401), OpenTelemetry .NET 1.19, Npgsql.OpenTelemetry 10.0.3, `System.Diagnostics.Metrics`, xUnit, Testcontainers, `MetricCollector` and `FakeLogger` from `Microsoft.Extensions.Diagnostics.Testing`, OpenTelemetry Collector contrib 0.162.0, Prometheus v3.15.0, Tempo 3.1.0, Loki 3.7.8, Grafana 13.2.3.

**Spec:** `docs/superpowers/specs/2026-10-12-observability-design.md`

## Facts checked while writing this plan

These were confirmed on 2026-10-12 with throwaway code outside the repository. Do not re-litigate them; build on them.

- `UseOtlpExporter()` reads `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` and `OTEL_METRIC_EXPORT_INTERVAL` from `IConfiguration`, and exports traces, metrics and logs.
- In the test host, `builder.UseSetting(key, value)` is visible to code in `Program.cs` that reads `builder.Configuration` before `builder.Build()`, and it wins over `appsettings.Development.json`. `builder.ConfigureAppConfiguration(...)` is **not** visible there. Telemetry settings in tests must therefore use `UseSetting`.
- A request sent to the test host with a `traceparent` header gets `Activity.Current.TraceId` equal to the id in the header.
- `Activity.SetTag(key, null)` adds no tag.
- A `[LoggerMessage]` method with `IReadOnlyCollection<Guid>` and `Guid?` parameters compiles and logs.
- The stack below (same images, same config files) received telemetry from a .NET app and returned it from Prometheus, Tempo and Loki. Prometheus named the lock histogram `ledger_account_lock_wait_duration_seconds` and exposed labels `kind`, `job="umutledger"` and `service_name`.
- Loki stores the trace id as structured metadata named `trace_id`, and the service as the label `service_name`.
- Tempo logs `error calling scheduler ... no jobs found` every 15 seconds and Loki logs `empty ring` once at startup. Both are harmless in single-container mode.

## Global Constraints

- Target framework is `net10.0`.
- Money is a `long` in minor units. No `decimal`, `double` or `float` in the money path. (`double` is allowed for the lock wait in seconds; that is a duration, not money.)
- Telemetry never changes a money result. `InstrumentedTransferService` returns the real service's result and rethrows its exceptions unchanged.
- Account ids, account names and idempotency keys are never metric labels. The idempotency key is never logged.
- `ledger.movements.amount` is increased only for a newly created transfer.
- Application code depends on `ILedgerMetrics`, never on OpenTelemetry types. `System.Diagnostics.Activity` and `ILogger` are allowed in `Application`.
- `Domain` references nothing outside itself.
- Time comes from the injected `TimeProvider`. Nothing calls `Stopwatch`, `DateTime.UtcNow` or `DateTimeOffset.UtcNow` in the API project.
- Tests use real PostgreSQL through Testcontainers. Nothing database-related is mocked.
- Tests need no Collector, Prometheus, Tempo, Loki or Grafana, and must not export to one that happens to be running.
- Tests never reset the database. Each test creates its own accounts and idempotency keys.
- Line and branch coverage must be 100%. The only exclusions are EF migration files and code marked `GeneratedCode`.
- Clean code and SOLID as defined in `CLAUDE.md`: intention-revealing names, small single-purpose functions, no duplication, no dead code, no magic numbers, comments only where code cannot explain itself.
- Every class is self-validating: no public setters, no public parameterless constructor that allows a faulty object.
- The API project uses block-scoped namespaces. The test project uses file-scoped namespaces.
- Docker image versions are pinned exactly as written in this plan. No `latest` tags.
- **No git steps.** `C:\UmutLedger` is not a git repository and the owner commits their own work. Never run `git init`, `git add`, `git commit`, or create branches or worktrees.
- Docker Desktop must be running for every task.
- All commands are PowerShell, run from `C:\UmutLedger`.

## Review Focus

1. **A Collector that is not running while the endpoint is configured** must not fail or visibly slow a request: a deposit still returns `201`. Pinned in Task 4.
2. **Two identical requests sent at the same time** must add the amount to `ledger.movements.amount` exactly once. Pinned in Task 3.
3. **A malformed `traceparent` header** must not break the request, and the error response must still carry a non-empty `traceId`. Pinned in Task 4.
4. **Running the tests while the local monitoring stack is up** must not send test telemetry into it. Pinned in Task 4.
5. **A movement refused before the lock query** (same account on both sides, zero amount) must record an outcome but no lock wait and no amount. Pinned in Task 2 and Task 3.

## File Structure

```
C:\UmutLedger\
├── docker-compose.yml                                   (modify) five monitoring services
├── README.md                                            (modify) run instructions
├── CLAUDE.md                                            (modify) tutorial steps
├── observability\
│   ├── otel-collector\config.yaml                       receive OTLP, forward each signal
│   ├── prometheus\prometheus.yml                        OTLP ingestion settings
│   ├── tempo\tempo.yaml                                 single-container trace store
│   ├── loki\loki.yaml                                   single-container log store
│   ├── grafana\provisioning\datasources\datasources.yaml
│   ├── grafana\provisioning\dashboards\dashboards.yaml
│   ├── grafana\dashboards\ledger-overview.json
│   └── smoke-test.ps1                                   end-to-end check of the stack
├── src\UmutLedger\
│   ├── UmutLedger.csproj                                (modify) four packages
│   ├── Program.cs                                       (modify) registrations
│   ├── appsettings.Development.json                     (modify) endpoint and export interval
│   ├── Application\Transfers\
│   │   ├── MovementKind.cs                              deposit, withdrawal, transfer
│   │   ├── MovementKindExtensions.cs                    kind -> label text
│   │   ├── ILedgerMetrics.cs                            the only metrics seam
│   │   ├── MovementOutcomes.cs                          outcome values and how to derive them
│   │   ├── MovementLog.cs                               the per-movement log line
│   │   ├── InstrumentedTransferService.cs               decorator
│   │   └── TransferService.cs                           (modify) lock timing only
│   ├── Infrastructure\Telemetry\LedgerMetrics.cs        meter, instruments, names
│   └── Api\
│       ├── Telemetry\LedgerTelemetry.cs                 SDK and exporter registration
│       └── Errors\LedgerProblem.cs                      (modify) traceId source
└── tests\UmutLedger.Tests\
    ├── UmutLedger.Tests.csproj                          (modify) one package
    ├── Support\RecordingLedgerMetrics.cs                in-memory ILedgerMetrics
    ├── Support\StubTransferService.cs                   inner service that returns or throws
    ├── Support\RequestSpanRecorder.cs                   captures request spans by trace id
    ├── Support\TraceContext.cs                          builds traceparent headers
    ├── Support\OtlpReceiver.cs                          tiny local OTLP/HTTP endpoint
    ├── Support\TestLedger.cs                            (modify) metrics parameter
    ├── Support\LedgerApi.cs                             (modify) Services, endpoint setting
    ├── Support\ApiExtensions.cs                         (modify) traceparent parameter
    ├── Application\MovementKindTests.cs
    ├── Application\MovementOutcomesTests.cs
    ├── Application\LockWaitTests.cs
    ├── Application\InstrumentedTransferServiceTests.cs
    ├── Application\TransferConcurrencyTests.cs          (modify) lock wait value test
    ├── Infrastructure\LedgerMetricsTests.cs
    ├── Api\LedgerProblemTests.cs
    ├── Api\LedgerTelemetryTests.cs
    ├── Api\MovementTelemetryApiTests.cs
    └── Api\TelemetryApiTests.cs
```

## Commands used throughout

- All tests with the coverage gate: `dotnet test -p:CollectCoverage=true`
- One test class: `dotnet test --filter "FullyQualifiedName~LedgerMetricsTests"`

---

### Task 1: Ledger metrics

**Files:**
- Create: `src/UmutLedger/Application/Transfers/MovementKind.cs`
- Create: `src/UmutLedger/Application/Transfers/MovementKindExtensions.cs`
- Create: `src/UmutLedger/Application/Transfers/ILedgerMetrics.cs`
- Create: `src/UmutLedger/Infrastructure/Telemetry/LedgerMetrics.cs`
- Modify: `tests/UmutLedger.Tests/UmutLedger.Tests.csproj`
- Test: `tests/UmutLedger.Tests/Application/MovementKindTests.cs`
- Test: `tests/UmutLedger.Tests/Infrastructure/LedgerMetricsTests.cs`

**Interfaces:**
- Consumes: `UmutLedger.Domain.Enums.Currency`, `UmutLedger.Domain.ValueObjects.Money` (`Amount`, `Currency`, `EnsurePositive()`).
- Produces:
  - `enum MovementKind { Deposit, Withdrawal, Transfer }`
  - `static string ToLabel(this MovementKind kind)` returning `deposit`, `withdrawal` or `transfer`
  - `interface ILedgerMetrics` with `void RecordMovement(MovementKind kind, Currency currency, string outcome)`, `void RecordAmount(MovementKind kind, Money amount)`, `void RecordLockWait(MovementKind kind, Currency currency, TimeSpan wait)`
  - `sealed class LedgerMetrics(IMeterFactory meterFactory) : ILedgerMetrics` with public constants `MeterName`, `MovementsCounter`, `AmountCounter`, `LockWaitHistogram`, `KindLabel`, `CurrencyLabel`, `OutcomeLabel`

- [ ] **Step 1: Add the test package**

In `tests/UmutLedger.Tests/UmutLedger.Tests.csproj`, add this line directly above the `Microsoft.Extensions.TimeProvider.Testing` reference:

```xml
    <PackageReference Include="Microsoft.Extensions.Diagnostics.Testing" Version="10.10.0" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/UmutLedger.Tests/Application/MovementKindTests.cs`:

```csharp
using UmutLedger.Application.Transfers;

namespace UmutLedger.Tests.Application;

public class MovementKindTests
{
    [Theory]
    [InlineData(MovementKind.Deposit, "deposit")]
    [InlineData(MovementKind.Withdrawal, "withdrawal")]
    [InlineData(MovementKind.Transfer, "transfer")]
    public void ToLabel_IsTheLowerCaseName(MovementKind kind, string expected) =>
        Assert.Equal(expected, kind.ToLabel());
}
```

Create `tests/UmutLedger.Tests/Infrastructure/LedgerMetricsTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Infrastructure.Telemetry;

namespace UmutLedger.Tests.Infrastructure;

public sealed class LedgerMetricsTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly IMeterFactory _meterFactory;
    private readonly LedgerMetrics _metrics;

    public LedgerMetricsTests()
    {
        _meterFactory = _services.GetRequiredService<IMeterFactory>();
        _metrics = new LedgerMetrics(_meterFactory);
    }

    public void Dispose() => _services.Dispose();

    private MetricCollector<T> Collect<T>(string instrumentName) where T : struct =>
        new(_meterFactory, LedgerMetrics.MeterName, instrumentName);

    [Fact]
    public void Constructor_WithoutAMeterFactory_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new LedgerMetrics(null!));

    [Fact]
    public void RecordMovement_AddsOneWithKindCurrencyAndOutcome()
    {
        using var collector = Collect<long>(LedgerMetrics.MovementsCounter);

        _metrics.RecordMovement(MovementKind.Withdrawal, Currency.EUR, "insufficient_funds");

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(3, measurement.Tags.Count);
        Assert.Equal("withdrawal", measurement.Tags[LedgerMetrics.KindLabel]);
        Assert.Equal("EUR", measurement.Tags[LedgerMetrics.CurrencyLabel]);
        Assert.Equal("insufficient_funds", measurement.Tags[LedgerMetrics.OutcomeLabel]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RecordMovement_WithoutAnOutcome_Throws(string? outcome) =>
        Assert.ThrowsAny<ArgumentException>(() => _metrics.RecordMovement(MovementKind.Deposit, Currency.USD, outcome!));

    [Fact]
    public void RecordAmount_AddsTheMinorUnitsWithKindAndCurrency()
    {
        using var collector = Collect<long>(LedgerMetrics.AmountCounter);

        _metrics.RecordAmount(MovementKind.Transfer, new Money(2500, Currency.GBP));

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(2500, measurement.Value);
        Assert.Equal(2, measurement.Tags.Count);
        Assert.Equal("transfer", measurement.Tags[LedgerMetrics.KindLabel]);
        Assert.Equal("GBP", measurement.Tags[LedgerMetrics.CurrencyLabel]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecordAmount_ThatIsNotPositive_Throws(long amount) =>
        Assert.Throws<ValidationFailedException>(() => _metrics.RecordAmount(MovementKind.Deposit, new Money(amount, Currency.USD)));

    [Fact]
    public void RecordAmount_WithoutAnAmount_Throws() =>
        Assert.Throws<ArgumentNullException>(() => _metrics.RecordAmount(MovementKind.Deposit, null!));

    [Fact]
    public void RecordLockWait_RecordsSecondsWithKindAndCurrency()
    {
        using var collector = Collect<double>(LedgerMetrics.LockWaitHistogram);

        _metrics.RecordLockWait(MovementKind.Deposit, Currency.USD, TimeSpan.FromMilliseconds(250));

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(0.25, measurement.Value);
        Assert.Equal(2, measurement.Tags.Count);
        Assert.Equal("deposit", measurement.Tags[LedgerMetrics.KindLabel]);
        Assert.Equal("USD", measurement.Tags[LedgerMetrics.CurrencyLabel]);
    }

    [Fact]
    public void RecordLockWait_ThatIsNegative_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _metrics.RecordLockWait(MovementKind.Deposit, Currency.USD, TimeSpan.FromTicks(-1)));

    // The unit decides the Prometheus name: "s" becomes the _seconds suffix the dashboard queries.
    [Fact]
    public void LockWaitHistogram_IsInSecondsWithBucketsFromOneMillisecondToTenSeconds()
    {
        using var collector = Collect<double>(LedgerMetrics.LockWaitHistogram);

        var histogram = Assert.IsType<Histogram<double>>(collector.Instrument);
        var buckets = histogram.Advice!.HistogramBucketBoundaries!;

        Assert.Equal("s", histogram.Unit);
        Assert.Equal(0.001, buckets[0]);
        Assert.Equal(10, buckets[^1]);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MovementKindTests|FullyQualifiedName~LedgerMetricsTests"`
Expected: build FAILS with errors such as `The type or namespace name 'MovementKind' could not be found`.

- [ ] **Step 4: Write the implementation**

Create `src/UmutLedger/Application/Transfers/MovementKind.cs`:

```csharp
namespace UmutLedger.Application.Transfers
{
    public enum MovementKind
    {
        Deposit,
        Withdrawal,
        Transfer
    }
}
```

Create `src/UmutLedger/Application/Transfers/MovementKindExtensions.cs`:

```csharp
namespace UmutLedger.Application.Transfers
{
    public static class MovementKindExtensions
    {
        public static string ToLabel(this MovementKind kind) => kind.ToString().ToLowerInvariant();
    }
}
```

Create `src/UmutLedger/Application/Transfers/ILedgerMetrics.cs`:

```csharp
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Application.Transfers
{
    public interface ILedgerMetrics
    {
        void RecordMovement(MovementKind kind, Currency currency, string outcome);

        void RecordAmount(MovementKind kind, Money amount);

        void RecordLockWait(MovementKind kind, Currency currency, TimeSpan wait);
    }
}
```

Create `src/UmutLedger/Infrastructure/Telemetry/LedgerMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Infrastructure.Telemetry
{
    public sealed class LedgerMetrics : ILedgerMetrics
    {
        public const string MeterName = "UmutLedger";
        public const string MovementsCounter = "ledger.movements";
        public const string AmountCounter = "ledger.movements.amount";
        public const string LockWaitHistogram = "ledger.account_lock.wait.duration";
        public const string KindLabel = "kind";
        public const string CurrencyLabel = "currency";
        public const string OutcomeLabel = "outcome";

        private const string MovementUnit = "{movement}";
        private const string MinorUnit = "{minor_unit}";
        private const string SecondsUnit = "s";
        private const long OneMovement = 1;

        private static readonly IReadOnlyList<double> LockWaitBucketsInSeconds =
            [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

        private readonly Counter<long> _movements;
        private readonly Counter<long> _amount;
        private readonly Histogram<double> _lockWait;

        public LedgerMetrics(IMeterFactory meterFactory)
        {
            ArgumentNullException.ThrowIfNull(meterFactory);
            var meter = meterFactory.Create(MeterName);
            _movements = meter.CreateCounter<long>(
                MovementsCounter, MovementUnit, "Money movement requests that reached the transfer service.");
            _amount = meter.CreateCounter<long>(
                AmountCounter, MinorUnit, "Money moved by newly created transfers, in minor units.");
            _lockWait = meter.CreateHistogram<double>(
                LockWaitHistogram,
                unit: SecondsUnit,
                description: "Time taken to acquire the account row locks.",
                advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LockWaitBucketsInSeconds });
        }

        public void RecordMovement(MovementKind kind, Currency currency, string outcome)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
            _movements.Add(OneMovement, Kind(kind), CurrencyOf(currency), Label(OutcomeLabel, outcome));
        }

        public void RecordAmount(MovementKind kind, Money amount)
        {
            ArgumentNullException.ThrowIfNull(amount);
            amount.EnsurePositive();
            _amount.Add(amount.Amount, Kind(kind), CurrencyOf(amount.Currency));
        }

        public void RecordLockWait(MovementKind kind, Currency currency, TimeSpan wait)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);
            _lockWait.Record(wait.TotalSeconds, Kind(kind), CurrencyOf(currency));
        }

        private static KeyValuePair<string, object?> Kind(MovementKind kind) => Label(KindLabel, kind.ToLabel());

        private static KeyValuePair<string, object?> CurrencyOf(Currency currency) => Label(CurrencyLabel, currency.ToString());

        private static KeyValuePair<string, object?> Label(string name, string value) => new(name, value);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MovementKindTests|FullyQualifiedName~LedgerMetricsTests"`
Expected: PASS, 15 tests.

- [ ] **Step 6: Run the whole suite with the coverage gate**

Run: `dotnet test -p:CollectCoverage=true`
Expected: all tests pass; line and branch coverage 100%.

---

### Task 2: Lock wait timing in `TransferService`

**Files:**
- Modify: `src/UmutLedger/Application/Transfers/TransferService.cs`
- Modify: `src/UmutLedger/Program.cs`
- Create: `tests/UmutLedger.Tests/Support/RecordingLedgerMetrics.cs`
- Modify: `tests/UmutLedger.Tests/Support/TestLedger.cs`
- Modify: `tests/UmutLedger.Tests/Application/TransferServiceTests.cs`
- Modify: `tests/UmutLedger.Tests/Application/TransferConcurrencyTests.cs`
- Test: `tests/UmutLedger.Tests/Application/LockWaitTests.cs`

**Interfaces:**
- Consumes: `ILedgerMetrics`, `MovementKind`, `LedgerMetrics` from Task 1.
- Produces:
  - `TransferService(LedgerDbContext db, TimeProvider timeProvider, ILedgerMetrics metrics)` (a third constructor parameter)
  - `RecordingLedgerMetrics : ILedgerMetrics` with `IReadOnlyCollection<RecordedMovement> Movements`, `IReadOnlyCollection<RecordedAmount> Amounts`, `IReadOnlyCollection<RecordedLockWait> LockWaits`
  - `record RecordedMovement(MovementKind Kind, Currency Currency, string Outcome)`, `record RecordedAmount(MovementKind Kind, Money Amount)`, `record RecordedLockWait(MovementKind Kind, Currency Currency, TimeSpan Wait)`
  - `TestLedger.WithTransferServiceAsync<TResult>(Func<ITransferService, Task<TResult>> action, TimeProvider? timeProvider = null, ILedgerMetrics? metrics = null)`
  - `ILedgerMetrics` registered in the container as a singleton `LedgerMetrics`

- [ ] **Step 1: Add the recording test double**

Create `tests/UmutLedger.Tests/Support/RecordingLedgerMetrics.cs`:

```csharp
using System.Collections.Concurrent;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Support;

public sealed record RecordedMovement(MovementKind Kind, Currency Currency, string Outcome);

public sealed record RecordedAmount(MovementKind Kind, Money Amount);

public sealed record RecordedLockWait(MovementKind Kind, Currency Currency, TimeSpan Wait);

// Thread-safe because the concurrency tests share one instance across parallel requests.
public sealed class RecordingLedgerMetrics : ILedgerMetrics
{
    private readonly ConcurrentQueue<RecordedMovement> _movements = new();
    private readonly ConcurrentQueue<RecordedAmount> _amounts = new();
    private readonly ConcurrentQueue<RecordedLockWait> _lockWaits = new();

    public IReadOnlyCollection<RecordedMovement> Movements => _movements.ToArray();

    public IReadOnlyCollection<RecordedAmount> Amounts => _amounts.ToArray();

    public IReadOnlyCollection<RecordedLockWait> LockWaits => _lockWaits.ToArray();

    public void RecordMovement(MovementKind kind, Currency currency, string outcome) =>
        _movements.Enqueue(new RecordedMovement(kind, currency, outcome));

    public void RecordAmount(MovementKind kind, Money amount) => _amounts.Enqueue(new RecordedAmount(kind, amount));

    public void RecordLockWait(MovementKind kind, Currency currency, TimeSpan wait) =>
        _lockWaits.Enqueue(new RecordedLockWait(kind, currency, wait));
}
```

- [ ] **Step 2: Let the test helper pass metrics to the service**

In `tests/UmutLedger.Tests/Support/TestLedger.cs`, replace the whole `WithTransferServiceAsync` method with:

```csharp
    public static async Task<TResult> WithTransferServiceAsync<TResult>(
        Func<ITransferService, Task<TResult>> action,
        TimeProvider? timeProvider = null,
        ILedgerMetrics? metrics = null)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        var service = new TransferService(db, timeProvider ?? TimeProvider.System, metrics ?? new RecordingLedgerMetrics());
        return await action(service);
    }
```

One existing test builds the service itself. In `tests/UmutLedger.Tests/Application/TransferServiceTests.cs`, replace the line

```csharp
        var service = new TransferService(db, TimeProvider.System);
```

with

```csharp
        var service = new TransferService(db, TimeProvider.System, new RecordingLedgerMetrics());
```

- [ ] **Step 3: Write the failing tests**

Create `tests/UmutLedger.Tests/Application/LockWaitTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class LockWaitTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Money Eur(long amount) => new(amount, Currency.EUR);

    private static Task<TResult> RunAsync<TResult>(
        RecordingLedgerMetrics metrics, Func<ITransferService, Task<TResult>> action, TimeProvider? clock = null) =>
        TestLedger.WithTransferServiceAsync(action, clock, metrics);

    [Fact]
    public async Task Deposit_RecordsOneLockWaitWithItsKindAndCurrency()
    {
        var accountId = await TestLedger.CreateCustomerAsync(Currency.EUR);
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.DepositAsync(TestLedger.NewKey(), accountId, Eur(500), None));

        var lockWait = Assert.Single(metrics.LockWaits);
        Assert.Equal(MovementKind.Deposit, lockWait.Kind);
        Assert.Equal(Currency.EUR, lockWait.Currency);
        Assert.True(lockWait.Wait >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Withdraw_RecordsOneLockWaitWithItsKindAndCurrency()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(500, Currency.EUR);
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.WithdrawAsync(TestLedger.NewKey(), accountId, Eur(200), None));

        var lockWait = Assert.Single(metrics.LockWaits);
        Assert.Equal(MovementKind.Withdrawal, lockWait.Kind);
        Assert.Equal(Currency.EUR, lockWait.Currency);
    }

    [Fact]
    public async Task Transfer_RecordsOneLockWaitWithItsKindAndCurrency()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(500);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service =>
            service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TestLedger.Usd(200), None));

        var lockWait = Assert.Single(metrics.LockWaits);
        Assert.Equal(MovementKind.Transfer, lockWait.Kind);
        Assert.Equal(Currency.USD, lockWait.Currency);
    }

    [Fact]
    public async Task ReplayFoundBeforeLocking_RecordsNoLockWait()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await RunAsync(new RecordingLedgerMetrics(), service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));
        var metrics = new RecordingLedgerMetrics();

        var replay = await RunAsync(metrics, service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));

        Assert.True(replay.WasReplayed);
        Assert.Empty(metrics.LockWaits);
    }

    [Fact]
    public async Task RejectedAfterLocking_StillRecordsTheLockWait()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        var metrics = new RecordingLedgerMetrics();

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            RunAsync(metrics, service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None)));

        Assert.Single(metrics.LockWaits);
    }

    [Fact]
    public async Task SameAccountTransfer_IsRefusedBeforeLocking_AndRecordsNoLockWait()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        var metrics = new RecordingLedgerMetrics();

        await Assert.ThrowsAsync<SameAccountTransferException>(() =>
            RunAsync(metrics, service => service.TransferAsync(TestLedger.NewKey(), accountId, accountId, TestLedger.Usd(10), None)));

        Assert.Empty(metrics.LockWaits);
    }

    [Fact]
    public async Task ZeroAmount_IsRefusedBeforeLocking_AndRecordsNoLockWait()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(metrics, service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(0), None)));

        Assert.Empty(metrics.LockWaits);
    }

    // A clock that never advances reports exactly zero, which the real clock never would.
    [Fact]
    public async Task LockWait_IsMeasuredWithTheInjectedClock()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();
        var frozenClock = new FakeTimeProvider(TestLedger.BaseTime);

        await RunAsync(metrics, service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(500), None), frozenClock);

        Assert.Equal(TimeSpan.Zero, Assert.Single(metrics.LockWaits).Wait);
    }
}
```

In `tests/UmutLedger.Tests/Application/TransferConcurrencyTests.cs`:

Add `using System.Diagnostics;` as the first `using` line, and add this field below `BlockedRequestPollInterval`:

```csharp
    private static readonly TimeSpan LockHeldFor = TimeSpan.FromMilliseconds(300);
```

Add this test after `DuplicateKeyRace_SameKeyOnUnrelatedAccounts_LoserIsStoppedByTheUniqueConstraint`:

```csharp
    // The measured wait started before the request was seen blocked and ended after the commit, so it contains the held interval.
    [Fact]
    public async Task LockWait_BehindAnotherTransaction_IsAtLeastAsLongAsTheLockWasHeld()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(TransferAmount);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await using var holderDb = await TestDatabase.CreateContextAsync();
        await using var holderTransaction = await holderDb.Database.BeginTransactionAsync();
        var holderProcessId = await BackendProcessIdAsync(holderDb);
        await LockAccountsAsync(holderDb, debitAccountId, creditAccountId);

        var blocked = Task.Run(() => TestLedger.WithTransferServiceAsync(
            service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TestLedger.Usd(TransferAmount), None),
            metrics: metrics));
        await WaitUntilARequestIsBlockedByAsync(holderProcessId);
        var heldSince = Stopwatch.GetTimestamp();
        await Task.Delay(LockHeldFor);
        var heldFor = Stopwatch.GetElapsedTime(heldSince);
        await holderTransaction.CommitAsync();
        await blocked;

        var lockWait = Assert.Single(metrics.LockWaits);
        Assert.True(lockWait.Wait >= heldFor, $"Recorded {lockWait.Wait} but the lock was held for {heldFor}.");
    }
```

Replace the whole `AddUncommittedTransferAsync` method with these two methods, so the lock query is written once:

```csharp
    private static async Task<Transfer> AddUncommittedTransferAsync(
        LedgerDbContext db, IdempotencyKey key, Guid debitAccountId, Guid creditAccountId)
    {
        var lockedAccounts = await LockAccountsAsync(db, debitAccountId, creditAccountId);
        var transfer = Transfer.Execute(
            Guid.CreateVersion7(),
            key,
            lockedAccounts.Single(account => account.Id == debitAccountId),
            lockedAccounts.Single(account => account.Id == creditAccountId),
            TestLedger.Usd(TransferAmount),
            TestLedger.BaseTime);
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync();
        return transfer;
    }

    private static Task<List<Account>> LockAccountsAsync(LedgerDbContext db, params Guid[] accountIds) =>
        db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({accountIds}) ORDER BY id FOR UPDATE")
            .ToListAsync();
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LockWaitTests"`
Expected: build FAILS with `'TransferService' does not contain a constructor that takes 3 arguments`.

- [ ] **Step 5: Time the lock query in `TransferService`**

In `src/UmutLedger/Application/Transfers/TransferService.cs`:

Change the class declaration to:

```csharp
    public sealed class TransferService(LedgerDbContext db, TimeProvider timeProvider, ILedgerMetrics metrics) : ITransferService
```

In `TransferAsync`, change the instruction line to:

```csharp
            var instruction = new Instruction(MovementKind.Transfer, key, debitAccountId, creditAccountId, amount, [debitAccountId, creditAccountId]);
```

In `DepositAsync`, change the return line to:

```csharp
            return await ExecuteAsync(new Instruction(MovementKind.Deposit, key, worldAccountId, accountId, amount, [accountId]), cancellationToken);
```

In `WithdrawAsync`, change the return line to:

```csharp
            return await ExecuteAsync(new Instruction(MovementKind.Withdrawal, key, accountId, worldAccountId, amount, [accountId]), cancellationToken);
```

Replace the whole `LockAccountsAsync` method (keep the comment above it) with:

```csharp
        private async Task<Dictionary<Guid, Account>> LockAccountsAsync(Instruction instruction, CancellationToken cancellationToken)
        {
            Guid[] accountIds = [instruction.DebitAccountId, instruction.CreditAccountId];
            var lockRequestedAt = timeProvider.GetTimestamp();
            var lockedAccounts = await db.Accounts
                .FromSql($"SELECT * FROM accounts WHERE id = ANY({accountIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(cancellationToken);
            metrics.RecordLockWait(instruction.Kind, instruction.Amount.Currency, timeProvider.GetElapsedTime(lockRequestedAt));
            return lockedAccounts.ToDictionary(account => account.Id);
        }
```

Replace the `Instruction` record with:

```csharp
        private sealed record Instruction(
            MovementKind Kind,
            IdempotencyKey Key,
            Guid DebitAccountId,
            Guid CreditAccountId,
            Money Amount,
            IReadOnlyCollection<Guid> AccountIdsNamedByTheCaller);
```

- [ ] **Step 6: Register the metrics in the container**

In `src/UmutLedger/Program.cs`, add `using UmutLedger.Infrastructure.Telemetry;` after the `using UmutLedger.Infrastructure.Persistence;` line, and add this line directly above `builder.Services.AddScoped<IAccountService, AccountService>();`:

```csharp
builder.Services.AddSingleton<ILedgerMetrics, LedgerMetrics>();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~LockWaitTests|FullyQualifiedName~TransferConcurrencyTests"`
Expected: PASS, 14 tests (8 in `LockWaitTests`, 6 in `TransferConcurrencyTests`).

- [ ] **Step 8: Run the whole suite with the coverage gate**

Run: `dotnet test -p:CollectCoverage=true`
Expected: all tests pass; line and branch coverage 100%.

---

### Task 3: Movement outcomes, volume, span attributes and the log line

**Files:**
- Create: `src/UmutLedger/Application/Transfers/MovementOutcomes.cs`
- Create: `src/UmutLedger/Application/Transfers/MovementLog.cs`
- Create: `src/UmutLedger/Application/Transfers/InstrumentedTransferService.cs`
- Modify: `src/UmutLedger/Program.cs`
- Create: `tests/UmutLedger.Tests/Support/StubTransferService.cs`
- Create: `tests/UmutLedger.Tests/Support/TraceContext.cs`
- Create: `tests/UmutLedger.Tests/Support/RequestSpanRecorder.cs`
- Modify: `tests/UmutLedger.Tests/Support/LedgerApi.cs`
- Modify: `tests/UmutLedger.Tests/Support/ApiExtensions.cs`
- Test: `tests/UmutLedger.Tests/Application/MovementOutcomesTests.cs`
- Test: `tests/UmutLedger.Tests/Application/InstrumentedTransferServiceTests.cs`
- Test: `tests/UmutLedger.Tests/Api/MovementTelemetryApiTests.cs`

**Interfaces:**
- Consumes: `ILedgerMetrics`, `MovementKind`, `ToLabel()` (Task 1); `TransferService(db, timeProvider, metrics)`, `RecordingLedgerMetrics`, `RecordedMovement`, `RecordedAmount` (Task 2); existing `ITransferService`, `TransferOutcome` (`Transfer`, `WasReplayed`), `LedgerException.Code`.
- Produces:
  - `static class MovementOutcomes` with constants `Created = "created"`, `Replayed = "replayed"`, `Cancelled = "cancelled"`, `Error = "error"`, and `static string Of(TransferOutcome outcome)`, `static string Of(Exception exception)`
  - `sealed class InstrumentedTransferService(ITransferService inner, ILedgerMetrics metrics, ILogger<InstrumentedTransferService> logger) : ITransferService` with public constants `KindAttribute = "ledger.movement.kind"`, `OutcomeAttribute = "ledger.movement.outcome"`, `TransferIdAttribute = "ledger.transfer.id"`
  - `ITransferService` resolves to `InstrumentedTransferService` wrapping `TransferService`
  - Test support: `LedgerApi.Services`, `TraceContext.NewTraceId()`, `TraceContext.ParentHeaderFor(string traceId)`, `TraceContext.HeaderName`, `RequestSpanRecorder.SpanOf(string traceId)`, `PostLedgerAsync(url, body, idempotencyKey, traceParent)`, `StubTransferService.Returning(TransferOutcome)`, `StubTransferService.Throwing(Exception)`

- [ ] **Step 1: Add the test support types**

Create `tests/UmutLedger.Tests/Support/StubTransferService.cs`:

```csharp
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Support;

// Stands in for the inner transfer service where a test needs an outcome the real one cannot be made to produce on demand.
public sealed class StubTransferService(Func<Task<TransferOutcome>> result) : ITransferService
{
    public static StubTransferService Returning(TransferOutcome outcome) => new(() => Task.FromResult(outcome));

    public static StubTransferService Throwing(Exception exception) => new(() => Task.FromException<TransferOutcome>(exception));

    public Task<TransferOutcome> TransferAsync(
        IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, Money amount, CancellationToken cancellationToken) => result();

    public Task<TransferOutcome> DepositAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken) => result();

    public Task<TransferOutcome> WithdrawAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken) => result();

    public Task<Transfer> GetAsync(Guid transferId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
```

Create `tests/UmutLedger.Tests/Support/TraceContext.cs`:

```csharp
using System.Diagnostics;

namespace UmutLedger.Tests.Support;

public static class TraceContext
{
    public const string HeaderName = "traceparent";

    public static string NewTraceId() => ActivityTraceId.CreateRandom().ToString();

    public static string ParentHeaderFor(string traceId) => $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01";
}
```

Create `tests/UmutLedger.Tests/Support/RequestSpanRecorder.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;

namespace UmutLedger.Tests.Support;

// The listener is process-wide, so spans are looked up by the trace id the test itself sent.
public sealed class RequestSpanRecorder : IDisposable
{
    private const string AspNetCoreSource = "Microsoft.AspNetCore";

    private readonly ConcurrentQueue<Activity> _started = new();
    private readonly ActivityListener _listener;

    public RequestSpanRecorder()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AspNetCoreSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _started.Enqueue
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Activity SpanOf(string traceId) => _started.Single(span => span.TraceId.ToString() == traceId);

    public void Dispose() => _listener.Dispose();
}
```

In `tests/UmutLedger.Tests/Support/LedgerApi.cs`, add this property below `Client`:

```csharp
    public IServiceProvider Services => _factory.Services;
```

In `tests/UmutLedger.Tests/Support/ApiExtensions.cs`, replace the whole `PostLedgerAsync` method with:

```csharp
    public static Task<HttpResponseMessage> PostLedgerAsync(
        this HttpClient client, string url, object body, string? idempotencyKey = null, string? traceParent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyKeyHeader, idempotencyKey);
        }

        if (traceParent is not null)
        {
            request.Headers.Add(TraceContext.HeaderName, traceParent);
        }

        return client.SendAsync(request);
    }
```

- [ ] **Step 2: Write the failing tests**

Create `tests/UmutLedger.Tests/Application/MovementOutcomesTests.cs`:

```csharp
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class MovementOutcomesTests
{
    private static async Task<TransferOutcome> OutcomeAsync(bool wasReplayed)
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        return new TransferOutcome(transfer, wasReplayed);
    }

    [Fact]
    public async Task Of_ANewTransfer_IsCreated() =>
        Assert.Equal("created", MovementOutcomes.Of(await OutcomeAsync(wasReplayed: false)));

    [Fact]
    public async Task Of_AReplayedTransfer_IsReplayed() =>
        Assert.Equal("replayed", MovementOutcomes.Of(await OutcomeAsync(wasReplayed: true)));

    [Fact]
    public void Of_ALedgerException_IsItsErrorCode() =>
        Assert.Equal(ErrorCodes.InsufficientFunds, MovementOutcomes.Of(new InsufficientFundsException(Guid.CreateVersion7())));

    [Fact]
    public void Of_ACancellation_IsCancelled() =>
        Assert.Equal("cancelled", MovementOutcomes.Of(new TaskCanceledException()));

    [Fact]
    public void Of_AnyOtherException_IsError() =>
        Assert.Equal("error", MovementOutcomes.Of(new InvalidOperationException("boom")));
}
```

Create `tests/UmutLedger.Tests/Application/InstrumentedTransferServiceTests.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class InstrumentedTransferServiceTests
{
    private const int ParallelRequests = 20;
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<TResult> RunAsync<TResult>(
        RecordingLedgerMetrics metrics,
        Func<ITransferService, Task<TResult>> action,
        FakeLogger<InstrumentedTransferService>? logger = null)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        var service = new InstrumentedTransferService(
            new TransferService(db, TimeProvider.System, metrics),
            metrics,
            logger ?? new FakeLogger<InstrumentedTransferService>());
        return await action(service);
    }

    private static InstrumentedTransferService Around(StubTransferService inner, RecordingLedgerMetrics metrics) =>
        new(inner, metrics, new FakeLogger<InstrumentedTransferService>());

    private static async Task AssertRejectedAsync<TException>(
        Func<ITransferService, Task<TransferOutcome>> movement, MovementKind kind, string errorCode)
        where TException : LedgerException
    {
        var metrics = new RecordingLedgerMetrics();

        await Assert.ThrowsAsync<TException>(() => RunAsync(metrics, movement));

        Assert.Equal(new RecordedMovement(kind, Currency.USD, errorCode), Assert.Single(metrics.Movements));
        Assert.Empty(metrics.Amounts);
    }

    [Fact]
    public async Task Deposit_ThatCreatesATransfer_RecordsCreatedAndTheAmount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(500), None));

        Assert.Equal(new RecordedMovement(MovementKind.Deposit, Currency.USD, MovementOutcomes.Created), Assert.Single(metrics.Movements));
        Assert.Equal(new RecordedAmount(MovementKind.Deposit, TestLedger.Usd(500)), Assert.Single(metrics.Amounts));
    }

    [Fact]
    public async Task Withdrawal_ThatCreatesATransfer_RecordsCreatedAndTheAmount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(500);
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(200), None));

        Assert.Equal(new RecordedMovement(MovementKind.Withdrawal, Currency.USD, MovementOutcomes.Created), Assert.Single(metrics.Movements));
        Assert.Equal(new RecordedAmount(MovementKind.Withdrawal, TestLedger.Usd(200)), Assert.Single(metrics.Amounts));
    }

    [Fact]
    public async Task Transfer_ThatCreatesATransfer_RecordsCreatedAndTheAmount()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(500);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service =>
            service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TestLedger.Usd(300), None));

        Assert.Equal(new RecordedMovement(MovementKind.Transfer, Currency.USD, MovementOutcomes.Created), Assert.Single(metrics.Movements));
        Assert.Equal(new RecordedAmount(MovementKind.Transfer, TestLedger.Usd(300)), Assert.Single(metrics.Amounts));
    }

    [Fact]
    public async Task Replay_RecordsReplayedAndNoAmount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await RunAsync(new RecordingLedgerMetrics(), service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));

        Assert.Equal(new RecordedMovement(MovementKind.Deposit, Currency.USD, MovementOutcomes.Replayed), Assert.Single(metrics.Movements));
        Assert.Empty(metrics.Amounts);
    }

    [Fact]
    public async Task SameAccountTransfer_RecordsItsErrorCodeAndNoAmount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        await AssertRejectedAsync<SameAccountTransferException>(
            service => service.TransferAsync(TestLedger.NewKey(), accountId, accountId, TestLedger.Usd(10), None),
            MovementKind.Transfer,
            ErrorCodes.SameAccountTransfer);
    }

    [Fact]
    public async Task ZeroAmount_RecordsValidationFailedAndNoAmount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        await AssertRejectedAsync<ValidationFailedException>(
            service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(0), None),
            MovementKind.Deposit,
            ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task UnknownAccount_RecordsAccountNotFoundAndNoAmount() =>
        await AssertRejectedAsync<AccountNotFoundException>(
            service => service.DepositAsync(TestLedger.NewKey(), Guid.CreateVersion7(), TestLedger.Usd(10), None),
            MovementKind.Deposit,
            ErrorCodes.AccountNotFound);

    [Fact]
    public async Task WrongCurrency_RecordsCurrencyMismatchAndNoAmount()
    {
        var euroAccountId = await TestLedger.CreateCustomerAsync(Currency.EUR);
        var dollarAccountId = await TestLedger.CreateFundedCustomerAsync(100);

        await AssertRejectedAsync<CurrencyMismatchException>(
            service => service.TransferAsync(TestLedger.NewKey(), dollarAccountId, euroAccountId, TestLedger.Usd(10), None),
            MovementKind.Transfer,
            ErrorCodes.CurrencyMismatch);
    }

    [Fact]
    public async Task Overdraft_RecordsInsufficientFundsAndNoAmount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        await AssertRejectedAsync<InsufficientFundsException>(
            service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None),
            MovementKind.Withdrawal,
            ErrorCodes.InsufficientFunds);
    }

    [Fact]
    public async Task WorldAccountNamedByTheCaller_RecordsSystemAccountNotAllowedAndNoAmount()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);
        var accountId = await TestLedger.CreateCustomerAsync();

        await AssertRejectedAsync<SystemAccountNotAllowedException>(
            service => service.TransferAsync(TestLedger.NewKey(), worldAccountId, accountId, TestLedger.Usd(10), None),
            MovementKind.Transfer,
            ErrorCodes.SystemAccountNotAllowed);
    }

    [Fact]
    public async Task KeyReusedForADifferentRequest_RecordsIdempotencyKeyReusedAndNoAmount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await RunAsync(new RecordingLedgerMetrics(), service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));

        await AssertRejectedAsync<IdempotencyKeyReusedException>(
            service => service.DepositAsync(key, accountId, TestLedger.Usd(501), None),
            MovementKind.Deposit,
            ErrorCodes.IdempotencyKeyReused);
    }

    [Fact]
    public async Task Cancellation_RecordsCancelledAndRethrowsTheSameException()
    {
        var cancellation = new OperationCanceledException();
        var metrics = new RecordingLedgerMetrics();
        var service = Around(StubTransferService.Throwing(cancellation), metrics);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.DepositAsync(TestLedger.NewKey(), Guid.CreateVersion7(), TestLedger.Usd(10), None));

        Assert.Same(cancellation, thrown);
        Assert.Equal(new RecordedMovement(MovementKind.Deposit, Currency.USD, MovementOutcomes.Cancelled), Assert.Single(metrics.Movements));
        Assert.Empty(metrics.Amounts);
    }

    [Fact]
    public async Task UnexpectedFailure_RecordsErrorAndRethrowsTheSameException()
    {
        var failure = new InvalidOperationException("boom");
        var metrics = new RecordingLedgerMetrics();
        var service = Around(StubTransferService.Throwing(failure), metrics);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.WithdrawAsync(TestLedger.NewKey(), Guid.CreateVersion7(), TestLedger.Usd(10), None));

        Assert.Same(failure, thrown);
        Assert.Equal(new RecordedMovement(MovementKind.Withdrawal, Currency.USD, MovementOutcomes.Error), Assert.Single(metrics.Movements));
        Assert.Empty(metrics.Amounts);
    }

    [Fact]
    public async Task Result_IsReturnedUnchanged()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        var expected = new TransferOutcome(transfer, wasReplayed: false);
        var service = Around(StubTransferService.Returning(expected), new RecordingLedgerMetrics());

        var actual = await service.TransferAsync(TestLedger.NewKey(), Guid.CreateVersion7(), accountId, TestLedger.Usd(100), None);

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Get_ReturnsTheTransferAndRecordsNothing()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        var metrics = new RecordingLedgerMetrics();

        var found = await RunAsync(metrics, service => service.GetAsync(transfer.Id, None));

        Assert.Equal(transfer.Id, found.Id);
        Assert.Empty(metrics.Movements);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequests_AddTheAmountExactlyOnce()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        var metrics = new RecordingLedgerMetrics();

        var requests = Enumerable.Range(0, ParallelRequests).Select(_ => Task.Run(() =>
            RunAsync(metrics, service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None))));
        await Task.WhenAll(requests);

        Assert.Equal(new RecordedAmount(MovementKind.Deposit, TestLedger.Usd(500)), Assert.Single(metrics.Amounts));
        Assert.Single(metrics.Movements, movement => movement.Outcome == MovementOutcomes.Created);
        Assert.Equal(ParallelRequests - 1, metrics.Movements.Count(movement => movement.Outcome == MovementOutcomes.Replayed));
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
    }

    [Fact]
    public async Task CreatedMovement_TagsTheCurrentSpanWithKindOutcomeAndTransferId()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        using var span = new Activity("request").Start();

        var outcome = await RunAsync(new RecordingLedgerMetrics(), service =>
            service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(500), None));

        Assert.Equal("deposit", span.GetTagItem(InstrumentedTransferService.KindAttribute));
        Assert.Equal(MovementOutcomes.Created, span.GetTagItem(InstrumentedTransferService.OutcomeAttribute));
        Assert.Equal(outcome.Transfer.Id.ToString(), span.GetTagItem(InstrumentedTransferService.TransferIdAttribute));
    }

    [Fact]
    public async Task RejectedMovement_TagsTheCurrentSpanWithoutATransferId()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        using var span = new Activity("request").Start();

        await Assert.ThrowsAsync<InsufficientFundsException>(() => RunAsync(new RecordingLedgerMetrics(), service =>
            service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None)));

        Assert.Equal("withdrawal", span.GetTagItem(InstrumentedTransferService.KindAttribute));
        Assert.Equal(ErrorCodes.InsufficientFunds, span.GetTagItem(InstrumentedTransferService.OutcomeAttribute));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Key == InstrumentedTransferService.TransferIdAttribute);
    }

    [Fact]
    public async Task CreatedMovement_WritesOneInformationLogLineWithoutTheIdempotencyKey()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        var logger = new FakeLogger<InstrumentedTransferService>();

        var outcome = await RunAsync(
            new RecordingLedgerMetrics(), service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None), logger);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Equal("deposit", record.GetStructuredStateValue("Kind"));
        Assert.Equal(MovementOutcomes.Created, record.GetStructuredStateValue("Outcome"));
        Assert.Equal("500", record.GetStructuredStateValue("Amount"));
        Assert.Equal("USD", record.GetStructuredStateValue("Currency"));
        Assert.Equal(outcome.Transfer.Id.ToString(), record.GetStructuredStateValue("TransferId"));
        Assert.Contains(accountId.ToString(), record.Message);
        Assert.DoesNotContain(key.Value, record.Message);
        Assert.DoesNotContain(record.StructuredState!, field => field.Value == key.Value);
    }

    [Fact]
    public async Task RejectedMovement_WritesOneLogLineWithItsErrorCode()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        var logger = new FakeLogger<InstrumentedTransferService>();

        await Assert.ThrowsAsync<InsufficientFundsException>(() => RunAsync(
            new RecordingLedgerMetrics(), service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None), logger));

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal("withdrawal", record.GetStructuredStateValue("Kind"));
        Assert.Equal(ErrorCodes.InsufficientFunds, record.GetStructuredStateValue("Outcome"));
    }
}
```

Create `tests/UmutLedger.Tests/Api/MovementTelemetryApiTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using UmutLedger.Application.Transfers;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class MovementTelemetryApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    [Fact]
    public void TransferService_IsResolvedAsTheInstrumentedDecorator()
    {
        using var scope = api.Services.CreateScope();

        Assert.IsType<InstrumentedTransferService>(scope.ServiceProvider.GetRequiredService<ITransferService>());
    }

    [Fact]
    public async Task Deposit_TagsItsRequestSpan()
    {
        using var spans = new RequestSpanRecorder();
        var accountId = await TestLedger.CreateCustomerAsync();
        var traceId = TraceContext.NewTraceId();

        var response = await api.Client.PostLedgerAsync(
            $"/accounts/{accountId}/deposits",
            new { amount = 500, currency = "USD" },
            Guid.NewGuid().ToString(),
            TraceContext.ParentHeaderFor(traceId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var span = spans.SpanOf(traceId);
        Assert.Equal("deposit", span.GetTagItem(InstrumentedTransferService.KindAttribute));
        Assert.Equal(MovementOutcomes.Created, span.GetTagItem(InstrumentedTransferService.OutcomeAttribute));
        Assert.NotNull(span.GetTagItem(InstrumentedTransferService.TransferIdAttribute));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MovementOutcomesTests|FullyQualifiedName~InstrumentedTransferServiceTests|FullyQualifiedName~MovementTelemetryApiTests"`
Expected: build FAILS with `The type or namespace name 'MovementOutcomes' could not be found` and `'InstrumentedTransferService' could not be found`.

- [ ] **Step 4: Write the implementation**

Create `src/UmutLedger/Application/Transfers/MovementOutcomes.cs`:

```csharp
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Application.Transfers
{
    public static class MovementOutcomes
    {
        public const string Created = "created";
        public const string Replayed = "replayed";
        public const string Cancelled = "cancelled";
        public const string Error = "error";

        public static string Of(TransferOutcome outcome) => outcome.WasReplayed ? Replayed : Created;

        public static string Of(Exception exception) => exception switch
        {
            LedgerException ledgerException => ledgerException.Code,
            OperationCanceledException => Cancelled,
            _ => Error
        };
    }
}
```

Create `src/UmutLedger/Application/Transfers/MovementLog.cs`:

```csharp
using UmutLedger.Domain.Enums;

namespace UmutLedger.Application.Transfers
{
    internal static partial class MovementLog
    {
        private const int MovementFinishedEventId = 1;

        [LoggerMessage(
            EventId = MovementFinishedEventId,
            Level = LogLevel.Information,
            Message = "Movement {Kind} finished as {Outcome}: {Amount} {Currency}, accounts {AccountIds}, transfer {TransferId}")]
        public static partial void MovementFinished(
            this ILogger logger,
            string kind,
            string outcome,
            long amount,
            Currency currency,
            IReadOnlyCollection<Guid> accountIds,
            Guid? transferId);
    }
}
```

Create `src/UmutLedger/Application/Transfers/InstrumentedTransferService.cs`:

```csharp
using System.Diagnostics;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Application.Transfers
{
    public sealed class InstrumentedTransferService(
        ITransferService inner, ILedgerMetrics metrics, ILogger<InstrumentedTransferService> logger) : ITransferService
    {
        public const string KindAttribute = "ledger.movement.kind";
        public const string OutcomeAttribute = "ledger.movement.outcome";
        public const string TransferIdAttribute = "ledger.transfer.id";

        public Task<TransferOutcome> TransferAsync(
            IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, Money amount, CancellationToken cancellationToken) =>
            ObserveAsync(
                new Movement(MovementKind.Transfer, amount, [debitAccountId, creditAccountId]),
                () => inner.TransferAsync(key, debitAccountId, creditAccountId, amount, cancellationToken));

        public Task<TransferOutcome> DepositAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken) =>
            ObserveAsync(
                new Movement(MovementKind.Deposit, amount, [accountId]),
                () => inner.DepositAsync(key, accountId, amount, cancellationToken));

        public Task<TransferOutcome> WithdrawAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken) =>
            ObserveAsync(
                new Movement(MovementKind.Withdrawal, amount, [accountId]),
                () => inner.WithdrawAsync(key, accountId, amount, cancellationToken));

        public Task<Transfer> GetAsync(Guid transferId, CancellationToken cancellationToken) =>
            inner.GetAsync(transferId, cancellationToken);

        private async Task<TransferOutcome> ObserveAsync(Movement movement, Func<Task<TransferOutcome>> execute)
        {
            TransferOutcome outcome;
            try
            {
                outcome = await execute();
            }
            catch (Exception exception)
            {
                Report(movement, MovementOutcomes.Of(exception), transferId: null);
                throw;
            }

            if (!outcome.WasReplayed)
            {
                metrics.RecordAmount(movement.Kind, movement.Amount);
            }

            Report(movement, MovementOutcomes.Of(outcome), outcome.Transfer.Id);
            return outcome;
        }

        private void Report(Movement movement, string outcome, Guid? transferId)
        {
            metrics.RecordMovement(movement.Kind, movement.Amount.Currency, outcome);
            TagRequestSpan(movement.Kind, outcome, transferId);
            logger.MovementFinished(
                movement.Kind.ToLabel(), outcome, movement.Amount.Amount, movement.Amount.Currency, movement.AccountIds, transferId);
        }

        private static void TagRequestSpan(MovementKind kind, string outcome, Guid? transferId)
        {
            if (Activity.Current is not { } requestSpan)
            {
                return;
            }

            requestSpan.SetTag(KindAttribute, kind.ToLabel());
            requestSpan.SetTag(OutcomeAttribute, outcome);
            requestSpan.SetTag(TransferIdAttribute, transferId?.ToString());
        }

        private sealed record Movement(MovementKind Kind, Money Amount, IReadOnlyCollection<Guid> AccountIds);
    }
}
```

- [ ] **Step 5: Register the decorator**

In `src/UmutLedger/Program.cs`, replace the line `builder.Services.AddScoped<ITransferService, TransferService>();` with:

```csharp
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<ITransferService>(services => new InstrumentedTransferService(
    services.GetRequiredService<TransferService>(),
    services.GetRequiredService<ILedgerMetrics>(),
    services.GetRequiredService<ILogger<InstrumentedTransferService>>()));
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MovementOutcomesTests|FullyQualifiedName~InstrumentedTransferServiceTests|FullyQualifiedName~MovementTelemetryApiTests"`
Expected: PASS, 27 tests (5 + 20 + 2).

- [ ] **Step 7: Run the whole suite with the coverage gate**

Run: `dotnet test -p:CollectCoverage=true`
Expected: all tests pass; line and branch coverage 100%. Every earlier API test now runs through the decorator, so a failure here means the decorator changed a result.

---

### Task 4: OpenTelemetry registration, export and the real trace id

**Files:**
- Modify: `src/UmutLedger/UmutLedger.csproj`
- Create: `src/UmutLedger/Api/Telemetry/LedgerTelemetry.cs`
- Modify: `src/UmutLedger/Program.cs`
- Modify: `src/UmutLedger/Api/Errors/LedgerProblem.cs`
- Modify: `src/UmutLedger/appsettings.Development.json`
- Create: `tests/UmutLedger.Tests/Support/OtlpReceiver.cs`
- Modify: `tests/UmutLedger.Tests/Support/LedgerApi.cs`
- Test: `tests/UmutLedger.Tests/Api/LedgerTelemetryTests.cs`
- Test: `tests/UmutLedger.Tests/Api/LedgerProblemTests.cs`
- Test: `tests/UmutLedger.Tests/Api/TelemetryApiTests.cs`

**Interfaces:**
- Consumes: `LedgerMetrics.MeterName`, `LedgerMetrics.MovementsCounter`, `LedgerMetrics.KindLabel`, `LedgerMetrics.OutcomeLabel` (Task 1); `LedgerApi.Services`, `TraceContext`, `PostLedgerAsync(url, body, idempotencyKey, traceParent)` (Task 3); `MovementOutcomes.Created` (Task 3).
- Produces:
  - `static class LedgerTelemetry` with `const string ServiceName = "umutledger"`, `const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT"`, `static IServiceCollection AddLedgerTelemetry(this IServiceCollection services, IConfiguration configuration)`, `static bool IsExportConfigured(IConfiguration configuration)`
  - `LedgerApi.CreateFactory(string connectionString, string environment, string otlpEndpoint = "")`
  - `OtlpReceiver.StartAsync()`, `OtlpReceiver.Url`, `OtlpReceiver.Signals`
  - `traceId` in problem details is the OpenTelemetry trace id

- [ ] **Step 1: Add the packages**

In `src/UmutLedger/UmutLedger.csproj`, add these four lines directly above the `Scalar.AspNetCore` reference:

```xml
    <PackageReference Include="Npgsql.OpenTelemetry" Version="10.0.3" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.19.1" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.19.1" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.19.0" />
```

- [ ] **Step 2: Add the local OTLP receiver and the endpoint setting for tests**

Create `tests/UmutLedger.Tests/Support/OtlpReceiver.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace UmutLedger.Tests.Support;

// Accepts OTLP over HTTP on a free local port and remembers which signals arrived.
public sealed class OtlpReceiver : IAsyncDisposable
{
    private const string AnyFreeLocalPort = "http://127.0.0.1:0";

    private readonly ConcurrentQueue<string> _signals = new();
    private readonly WebApplication _app;

    private OtlpReceiver()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(AnyFreeLocalPort);
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapPost("/v1/{signal}", (string signal) =>
        {
            _signals.Enqueue(signal);
            return Results.Ok();
        });
    }

    public string Url => _app.Urls.Single();

    public IReadOnlyList<string> Signals => _signals.Distinct().Order().ToList();

    public static async Task<OtlpReceiver> StartAsync()
    {
        var receiver = new OtlpReceiver();
        await receiver._app.StartAsync();
        return receiver;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
```

In `tests/UmutLedger.Tests/Support/LedgerApi.cs`, add `using UmutLedger.Api.Telemetry;` after the last `using` line, add these constants above the `_factory` field:

```csharp
    private const string NoExport = "";
    private const string OtlpProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string HttpProtobuf = "http/protobuf";
```

and replace the whole `CreateFactory` method with:

```csharp
    public static WebApplicationFactory<Program> CreateFactory(
        string connectionString, string environment, string otlpEndpoint = NoExport) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // UseSetting is visible to Program.cs before the host is built; ConfigureAppConfiguration is not.
            // An empty endpoint also overrides appsettings.Development.json, so tests never export to a running Collector.
            builder.UseSetting(LedgerTelemetry.OtlpEndpointKey, otlpEndpoint);
            builder.UseSetting(OtlpProtocolKey, HttpProtobuf);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString }));
            builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(ThrowingController).Assembly));
        });
```

- [ ] **Step 3: Write the failing tests**

Create `tests/UmutLedger.Tests/Api/LedgerTelemetryTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using UmutLedger.Api.Telemetry;

namespace UmutLedger.Tests.Api;

public class LedgerTelemetryTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("http://localhost:4317", true)]
    public void IsExportConfigured_OnlyWhenAnEndpointIsSet(string? endpoint, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [LedgerTelemetry.OtlpEndpointKey] = endpoint })
            .Build();

        Assert.Equal(expected, LedgerTelemetry.IsExportConfigured(configuration));
    }
}
```

Create `tests/UmutLedger.Tests/Api/LedgerProblemTests.cs`:

```csharp
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UmutLedger.Api.Errors;

namespace UmutLedger.Tests.Api;

public class LedgerProblemTests
{
    private const string RequestIdentifier = "request-1";

    private static ProblemDetails CreateProblem() => LedgerProblem.Create(
        new DefaultHttpContext { TraceIdentifier = RequestIdentifier },
        StatusCodes.Status400BadRequest,
        "validation_failed",
        "detail");

    [Fact]
    public void WithAnActiveTrace_TraceIdIsThatTracesId()
    {
        using var span = new Activity("request").Start();

        Assert.Equal(span.TraceId.ToString(), CreateProblem().Extensions["traceId"]);
    }

    [Fact]
    public void WithoutAnActiveTrace_TraceIdIsTheRequestIdentifier()
    {
        Activity.Current = null;

        Assert.Equal(RequestIdentifier, CreateProblem().Extensions["traceId"]);
    }
}
```

Create `tests/UmutLedger.Tests/Api/TelemetryApiTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using UmutLedger.Api.Telemetry;
using UmutLedger.Application.Transfers;
using UmutLedger.Infrastructure.Telemetry;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class TelemetryApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private const string UnreachableCollector = "http://127.0.0.1:1";
    private const string MalformedTraceParent = "not-a-trace";
    private const int FlushTimeoutMilliseconds = 10_000;

    private static HttpRequestMessage GetUnknownAccount(string traceParent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/accounts/{Guid.CreateVersion7()}");
        request.Headers.Add(TraceContext.HeaderName, traceParent);
        return request;
    }

    private static async Task<HttpResponseMessage> DepositAsync(HttpClient client)
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        return await client.PostLedgerAsync(
            $"/accounts/{accountId}/deposits", new { amount = 500, currency = "USD" }, Guid.NewGuid().ToString());
    }

    [Fact]
    public async Task ErrorResponse_TraceIdIsTheTraceIdOfTheRequest()
    {
        var traceId = TraceContext.NewTraceId();

        var response = await api.Client.SendAsync(GetUnknownAccount(TraceContext.ParentHeaderFor(traceId)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.ReadAsync<JsonElement>();
        Assert.Equal(traceId, problem.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task ErrorResponse_WithAMalformedTraceParent_StillCarriesATraceId()
    {
        var response = await api.Client.SendAsync(GetUnknownAccount(MalformedTraceParent));

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
    }

    [Fact]
    public async Task Deposit_IsCountedByTheApplicationsOwnMeter()
    {
        using var movements = new MetricCollector<long>(
            api.Services.GetRequiredService<IMeterFactory>(), LedgerMetrics.MeterName, LedgerMetrics.MovementsCounter);

        var response = await DepositAsync(api.Client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(movements.GetMeasurementSnapshot(), measurement =>
            Equals(measurement.Tags[LedgerMetrics.KindLabel], "deposit") &&
            Equals(measurement.Tags[LedgerMetrics.OutcomeLabel], MovementOutcomes.Created));
    }

    [Fact]
    public void TestHost_DoesNotExportByDefault() =>
        Assert.False(LedgerTelemetry.IsExportConfigured(api.Services.GetRequiredService<IConfiguration>()));

    [Fact]
    public async Task ConfiguredEndpoint_ReceivesTracesMetricsAndLogs()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var factory = LedgerApi.CreateFactory(
            await TestDatabase.GetConnectionStringAsync(), Environments.Development, receiver.Url);
        using var client = factory.CreateClient();

        var response = await DepositAsync(client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush(FlushTimeoutMilliseconds));
        Assert.True(factory.Services.GetRequiredService<MeterProvider>().ForceFlush(FlushTimeoutMilliseconds));
        Assert.True(factory.Services.GetRequiredService<LoggerProvider>().ForceFlush(FlushTimeoutMilliseconds));
        Assert.Equal(new[] { "logs", "metrics", "traces" }, receiver.Signals);
    }

    [Fact]
    public async Task UnreachableCollector_DoesNotFailARequest()
    {
        await using var factory = LedgerApi.CreateFactory(
            await TestDatabase.GetConnectionStringAsync(), Environments.Development, UnreachableCollector);
        using var client = factory.CreateClient();

        var response = await DepositAsync(client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LedgerTelemetryTests|FullyQualifiedName~LedgerProblemTests|FullyQualifiedName~TelemetryApiTests"`
Expected: build FAILS with `The type or namespace name 'Telemetry' does not exist in the namespace 'UmutLedger.Api'`.

- [ ] **Step 5: Write the registration**

Create `src/UmutLedger/Api/Telemetry/LedgerTelemetry.cs`:

```csharp
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using UmutLedger.Infrastructure.Telemetry;

namespace UmutLedger.Api.Telemetry
{
    public static class LedgerTelemetry
    {
        public const string ServiceName = "umutledger";
        public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

        private const string NpgsqlMeterName = "Npgsql";
        private const string RuntimeMeterName = "System.Runtime";

        private static readonly string ServiceVersion = typeof(LedgerTelemetry).Assembly.GetName().Version!.ToString();

        public static IServiceCollection AddLedgerTelemetry(this IServiceCollection services, IConfiguration configuration)
        {
            var telemetry = services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: ServiceVersion))
                .WithMetrics(metrics => metrics
                    .AddAspNetCoreInstrumentation()
                    .AddMeter(LedgerMetrics.MeterName, NpgsqlMeterName, RuntimeMeterName))
                .WithTracing(tracing => tracing
                    .AddAspNetCoreInstrumentation()
                    .AddNpgsql())
                .WithLogging(configureBuilder: null, configureOptions: options => options.IncludeFormattedMessage = true);

            if (IsExportConfigured(configuration))
            {
                telemetry.UseOtlpExporter();
            }

            return services;
        }

        public static bool IsExportConfigured(IConfiguration configuration) =>
            !string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]);
    }
}
```

In `src/UmutLedger/Program.cs`, add `using UmutLedger.Api.Telemetry;` after the `using UmutLedger.Api.Errors;` line, and add this line directly above `var app = builder.Build();`:

```csharp
builder.Services.AddLedgerTelemetry(builder.Configuration);
```

- [ ] **Step 6: Use the trace id in problem details**

In `src/UmutLedger/Api/Errors/LedgerProblem.cs`, add `using System.Diagnostics;` as the first `using` line, replace the line

```csharp
            problem.Extensions[TraceIdExtension] = httpContext.TraceIdentifier;
```

with

```csharp
            problem.Extensions[TraceIdExtension] = TraceIdOf(httpContext);
```

and add this method below `WriteAsync`:

```csharp
        private static string TraceIdOf(HttpContext httpContext) =>
            Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
```

- [ ] **Step 7: Configure the endpoint for local development**

Replace the whole content of `src/UmutLedger/appsettings.Development.json` with:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ConnectionStrings": {
    "Ledger": "Host=localhost;Port=5432;Database=umutledger;Username=umutledger;Password=umutledger"
  },
  "OTEL_EXPORTER_OTLP_ENDPOINT": "http://localhost:4317",
  "OTEL_METRIC_EXPORT_INTERVAL": "5000"
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~LedgerTelemetryTests|FullyQualifiedName~LedgerProblemTests|FullyQualifiedName~TelemetryApiTests"`
Expected: PASS, 12 tests (4 + 2 + 6).

- [ ] **Step 9: Run the whole suite with the coverage gate**

Run: `dotnet test -p:CollectCoverage=true`
Expected: all tests pass; line and branch coverage 100%.

---

### Task 5: The monitoring stack, dashboard, smoke test and documentation

**Files:**
- Create: `observability/otel-collector/config.yaml`
- Create: `observability/prometheus/prometheus.yml`
- Create: `observability/tempo/tempo.yaml`
- Create: `observability/loki/loki.yaml`
- Create: `observability/grafana/provisioning/datasources/datasources.yaml`
- Create: `observability/grafana/provisioning/dashboards/dashboards.yaml`
- Create: `observability/grafana/dashboards/ledger-overview.json`
- Create: `observability/smoke-test.ps1`
- Modify: `docker-compose.yml`
- Modify: `README.md`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: the API from Task 4, exporting OTLP over gRPC to `http://localhost:4317` in the Development environment with `service.name = umutledger`; the metrics `ledger.movements`, `ledger.movements.amount`, `ledger.account_lock.wait.duration` with labels `kind`, `currency`, `outcome`.
- Produces: Grafana at `http://localhost:3000` with data sources whose uids are `prometheus`, `tempo` and `loki`; Prometheus at `http://localhost:9090`; `observability/smoke-test.ps1`.

- [ ] **Step 1: Write the Collector configuration**

Create `observability/otel-collector/config.yaml`:

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317

processors:
  memory_limiter:
    check_interval: 1s
    limit_percentage: 80
    spike_limit_percentage: 20
  batch: {}

exporters:
  otlp_http/prometheus:
    endpoint: http://prometheus:9090/api/v1/otlp
  otlp_grpc/tempo:
    endpoint: tempo:4317
    tls:
      insecure: true
  otlp_http/loki:
    endpoint: http://loki:3100/otlp

service:
  pipelines:
    metrics:
      receivers: [otlp]
      processors: [memory_limiter, batch]
      exporters: [otlp_http/prometheus]
    traces:
      receivers: [otlp]
      processors: [memory_limiter, batch]
      exporters: [otlp_grpc/tempo]
    logs:
      receivers: [otlp]
      processors: [memory_limiter, batch]
      exporters: [otlp_http/loki]
```

The exporter type names `otlp_http` and `otlp_grpc` are the current names in Collector 0.162.0. The older names `otlphttp` and `otlp` still work there but log a deprecation warning. If the Collector container exits at startup with an unknown-exporter error, switch to the older names.

- [ ] **Step 2: Write the Prometheus, Tempo and Loki configuration**

Create `observability/prometheus/prometheus.yml`:

```yaml
otlp:
  promote_resource_attributes:
    - service.name
    - service.version

storage:
  tsdb:
    out_of_order_time_window: 30m
```

Create `observability/tempo/tempo.yaml`:

```yaml
server:
  http_listen_port: 3200

distributor:
  receivers:
    otlp:
      protocols:
        grpc:
          endpoint: 0.0.0.0:4317

storage:
  trace:
    backend: local
    local:
      path: /var/tempo/blocks
    wal:
      path: /var/tempo/wal
```

Create `observability/loki/loki.yaml`:

```yaml
auth_enabled: false

server:
  http_listen_port: 3100

common:
  instance_addr: 127.0.0.1
  path_prefix: /loki
  storage:
    filesystem:
      chunks_directory: /loki/chunks
      rules_directory: /loki/rules
  replication_factor: 1
  ring:
    kvstore:
      store: inmemory

schema_config:
  configs:
    - from: 2024-01-01
      store: tsdb
      object_store: filesystem
      schema: v13
      index:
        prefix: index_
        period: 24h

limits_config:
  allow_structured_metadata: true
```

- [ ] **Step 3: Write the Grafana provisioning**

Create `observability/grafana/provisioning/datasources/datasources.yaml`:

```yaml
apiVersion: 1

datasources:
  - name: Prometheus
    uid: prometheus
    type: prometheus
    access: proxy
    url: http://prometheus:9090
    isDefault: true
    jsonData:
      timeInterval: 5s
  - name: Tempo
    uid: tempo
    type: tempo
    access: proxy
    url: http://tempo:3200
    jsonData:
      tracesToLogsV2:
        datasourceUid: loki
        customQuery: true
        query: '{service_name="umutledger"} | trace_id="$${__span.traceId}"'
  - name: Loki
    uid: loki
    type: loki
    access: proxy
    url: http://loki:3100
    jsonData:
      derivedFields:
        - name: Trace
          matcherType: label
          matcherRegex: trace_id
          datasourceUid: tempo
          url: '$${__value.raw}'
```

The doubled `$$` stops Grafana from treating `${...}` as an environment variable while it reads the file.

Create `observability/grafana/provisioning/dashboards/dashboards.yaml`:

```yaml
apiVersion: 1

providers:
  - name: UmutLedger
    type: file
    allowUiUpdates: false
    options:
      path: /var/lib/grafana/dashboards
```

- [ ] **Step 4: Write the dashboard**

Create `observability/grafana/dashboards/ledger-overview.json`:

```json
{
  "uid": "ledger-overview",
  "title": "Ledger overview",
  "tags": ["umutledger"],
  "timezone": "browser",
  "schemaVersion": 41,
  "refresh": "10s",
  "time": { "from": "now-15m", "to": "now" },
  "panels": [
    { "id": 1, "type": "row", "title": "Traffic", "collapsed": false, "gridPos": { "h": 1, "w": 24, "x": 0, "y": 0 }, "panels": [] },
    {
      "id": 2, "type": "timeseries", "title": "Requests per second by route",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 8, "x": 0, "y": 1 },
      "fieldConfig": { "defaults": { "unit": "reqps" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "sum by (http_request_method, http_route) (rate(http_server_request_duration_seconds_count{job=\"umutledger\"}[$__rate_interval]))", "legendFormat": "{{http_request_method}} {{http_route}}" }
      ]
    },
    {
      "id": 3, "type": "timeseries", "title": "Error rate",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 8, "x": 8, "y": 1 },
      "fieldConfig": { "defaults": { "unit": "reqps" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "sum(rate(http_server_request_duration_seconds_count{job=\"umutledger\", http_response_status_code=~\"4..\"}[$__rate_interval]))", "legendFormat": "4xx" },
        { "refId": "B", "expr": "sum(rate(http_server_request_duration_seconds_count{job=\"umutledger\", http_response_status_code=~\"5..\"}[$__rate_interval]))", "legendFormat": "5xx" }
      ]
    },
    {
      "id": 4, "type": "timeseries", "title": "Request latency by route",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 8, "x": 16, "y": 1 },
      "fieldConfig": { "defaults": { "unit": "s" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "histogram_quantile(0.50, sum by (le, http_route) (rate(http_server_request_duration_seconds_bucket{job=\"umutledger\"}[$__rate_interval])))", "legendFormat": "p50 {{http_route}}" },
        { "refId": "B", "expr": "histogram_quantile(0.95, sum by (le, http_route) (rate(http_server_request_duration_seconds_bucket{job=\"umutledger\"}[$__rate_interval])))", "legendFormat": "p95 {{http_route}}" },
        { "refId": "C", "expr": "histogram_quantile(0.99, sum by (le, http_route) (rate(http_server_request_duration_seconds_bucket{job=\"umutledger\"}[$__rate_interval])))", "legendFormat": "p99 {{http_route}}" }
      ]
    },
    { "id": 5, "type": "row", "title": "Money movements", "collapsed": false, "gridPos": { "h": 1, "w": 24, "x": 0, "y": 9 }, "panels": [] },
    {
      "id": 6, "type": "timeseries", "title": "Movements per second by kind and outcome",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 6, "x": 0, "y": 10 },
      "fieldConfig": { "defaults": { "unit": "ops" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "sum by (kind, outcome) (rate(ledger_movements_total[$__rate_interval]))", "legendFormat": "{{kind}} {{outcome}}" }
      ]
    },
    {
      "id": 7, "type": "timeseries", "title": "Share of movements that were replays",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 6, "x": 6, "y": 10 },
      "fieldConfig": { "defaults": { "unit": "percentunit", "min": 0, "max": 1 }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "sum(rate(ledger_movements_total{outcome=\"replayed\"}[$__rate_interval])) / sum(rate(ledger_movements_total[$__rate_interval]))", "legendFormat": "replayed" }
      ]
    },
    {
      "id": 8, "type": "timeseries", "title": "Rejections per second by error code",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 6, "x": 12, "y": 10 },
      "fieldConfig": { "defaults": { "unit": "ops" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "sum by (outcome) (rate(ledger_movements_total{outcome!~\"created|replayed\"}[$__rate_interval]))", "legendFormat": "{{outcome}}" }
      ]
    },
    {
      "id": 9, "type": "timeseries", "title": "Money moved per second (minor units)",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 6, "x": 18, "y": 10 },
      "fieldConfig": { "defaults": { "unit": "short" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "sum by (currency, kind) (rate(ledger_movements_amount_total[$__rate_interval]))", "legendFormat": "{{currency}} {{kind}}" }
      ]
    },
    { "id": 10, "type": "row", "title": "Locking", "collapsed": false, "gridPos": { "h": 1, "w": 24, "x": 0, "y": 18 }, "panels": [] },
    {
      "id": 11, "type": "timeseries", "title": "Account lock wait by kind and currency",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 24, "x": 0, "y": 19 },
      "fieldConfig": { "defaults": { "unit": "s" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "histogram_quantile(0.50, sum by (le, kind, currency) (rate(ledger_account_lock_wait_duration_seconds_bucket[$__rate_interval])))", "legendFormat": "p50 {{kind}} {{currency}}" },
        { "refId": "B", "expr": "histogram_quantile(0.95, sum by (le, kind, currency) (rate(ledger_account_lock_wait_duration_seconds_bucket[$__rate_interval])))", "legendFormat": "p95 {{kind}} {{currency}}" },
        { "refId": "C", "expr": "histogram_quantile(0.99, sum by (le, kind, currency) (rate(ledger_account_lock_wait_duration_seconds_bucket[$__rate_interval])))", "legendFormat": "p99 {{kind}} {{currency}}" }
      ]
    },
    { "id": 12, "type": "row", "title": "Database and runtime", "collapsed": false, "gridPos": { "h": 1, "w": 24, "x": 0, "y": 27 }, "panels": [] },
    {
      "id": 13, "type": "timeseries", "title": "PostgreSQL connections in use",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 6, "x": 0, "y": 28 },
      "fieldConfig": { "defaults": { "unit": "short" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "sum(db_client_connection_count{job=\"umutledger\", db_client_connection_state=\"used\"})", "legendFormat": "in use" }
      ]
    },
    {
      "id": 14, "type": "timeseries", "title": "SQL command duration p95",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 6, "x": 6, "y": 28 },
      "fieldConfig": { "defaults": { "unit": "s" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "histogram_quantile(0.95, sum by (le) (rate(db_client_operation_duration_seconds_bucket{job=\"umutledger\"}[$__rate_interval])))", "legendFormat": "p95" }
      ]
    },
    {
      "id": 15, "type": "timeseries", "title": "Heap size after the last collection",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 6, "x": 12, "y": 28 },
      "fieldConfig": { "defaults": { "unit": "bytes" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "sum(dotnet_gc_last_collection_heap_size_bytes{job=\"umutledger\"})", "legendFormat": "heap" }
      ]
    },
    {
      "id": 16, "type": "timeseries", "title": "Garbage collections per second",
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "gridPos": { "h": 8, "w": 6, "x": 18, "y": 28 },
      "fieldConfig": { "defaults": { "unit": "ops" }, "overrides": [] },
      "targets": [
        { "refId": "A", "expr": "sum by (gc_heap_generation) (rate(dotnet_gc_collections_total{job=\"umutledger\"}[$__rate_interval]))", "legendFormat": "{{gc_heap_generation}}" }
      ]
    },
    { "id": 17, "type": "row", "title": "Logs", "collapsed": false, "gridPos": { "h": 1, "w": 24, "x": 0, "y": 36 }, "panels": [] },
    {
      "id": 18, "type": "logs", "title": "Latest warnings and errors",
      "datasource": { "type": "loki", "uid": "loki" },
      "gridPos": { "h": 10, "w": 24, "x": 0, "y": 37 },
      "options": { "showTime": true, "wrapLogMessage": true, "sortOrder": "Descending", "enableLogDetails": true },
      "targets": [
        { "refId": "A", "expr": "{service_name=\"umutledger\"} | detected_level=~\"warn|error|fatal|critical\"" }
      ]
    }
  ]
}
```

- [ ] **Step 5: Add the services to `docker-compose.yml`**

Replace the whole content of `docker-compose.yml` with:

```yaml
services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_DB: umutledger
      POSTGRES_USER: umutledger
      POSTGRES_PASSWORD: umutledger
    ports:
      - "5432:5432"
    volumes:
      - umutledger-postgres:/var/lib/postgresql/data

  otel-collector:
    image: otel/opentelemetry-collector-contrib:0.162.0
    command: ["--config=/etc/otelcol/config.yaml"]
    volumes:
      - ./observability/otel-collector/config.yaml:/etc/otelcol/config.yaml:ro
    ports:
      - "4317:4317"
    depends_on:
      - prometheus
      - tempo
      - loki

  prometheus:
    image: prom/prometheus:v3.15.0
    command:
      - --config.file=/etc/prometheus/prometheus.yml
      - --storage.tsdb.path=/prometheus
      - --web.enable-otlp-receiver
    volumes:
      - ./observability/prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
      - umutledger-prometheus:/prometheus
    ports:
      - "9090:9090"

  tempo:
    image: grafana/tempo:3.1.0
    command: ["-config.file=/etc/tempo/tempo.yaml"]
    volumes:
      - ./observability/tempo/tempo.yaml:/etc/tempo/tempo.yaml:ro
      - umutledger-tempo:/var/tempo

  loki:
    image: grafana/loki:3.7.8
    command: ["-config.file=/etc/loki/loki.yaml"]
    volumes:
      - ./observability/loki/loki.yaml:/etc/loki/loki.yaml:ro
      - umutledger-loki:/loki

  grafana:
    image: grafana/grafana:13.2.3
    environment:
      GF_AUTH_ANONYMOUS_ENABLED: "true"
      GF_AUTH_ANONYMOUS_ORG_ROLE: Admin
      GF_AUTH_DISABLE_LOGIN_FORM: "true"
    volumes:
      - ./observability/grafana/provisioning:/etc/grafana/provisioning:ro
      - ./observability/grafana/dashboards:/var/lib/grafana/dashboards:ro
    ports:
      - "3000:3000"
    depends_on:
      - prometheus
      - tempo
      - loki

volumes:
  umutledger-postgres:
  umutledger-prometheus:
  umutledger-tempo:
  umutledger-loki:
```

- [ ] **Step 6: Start the stack and check that every container stays up**

Run:

```powershell
docker compose up -d
Start-Sleep -Seconds 20
docker compose ps --format "{{.Service}}  {{.Status}}"
```

Expected: six lines, each starting with a service name followed by `Up`. If a service is missing or shows `Restarting` or `Exited`, read its log with `docker compose logs <service>` and fix its configuration file before continuing. Tempo's repeating `no jobs found` lines and Loki's single `empty ring` line are expected.

- [ ] **Step 7: Write the smoke test**

Create `observability/smoke-test.ps1`:

```powershell
#Requires -Version 5.1
# Checks the whole telemetry path: API -> Collector -> Prometheus, Tempo and Loki.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File observability\smoke-test.ps1

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$apiPort = 5289
$apiUrl = "http://localhost:$apiPort"
$prometheusUrl = "http://localhost:9090"
$grafanaUrl = "http://localhost:3000"
$timeoutSeconds = 120
$pollSeconds = 3
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
Invoke-Native "database migration" { dotnet ef database update --project src/UmutLedger }
Invoke-Native "build" { dotnet build src/UmutLedger --nologo --verbosity quiet }

$totalsBefore = @{}
foreach ($query in $counterQueries) {
    $totalsBefore[$query] = Get-PrometheusTotal $query
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = $apiUrl
$apiProcess = Start-Process -FilePath "dotnet" -ArgumentList "bin\Debug\net10.0\UmutLedger.dll" `
    -WorkingDirectory (Join-Path $repositoryRoot "src\UmutLedger") -PassThru -WindowStyle Hidden

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

    $deposit = Send-Json "/accounts/$($alice.Body.id)/deposits" @{ amount = 10000; currency = "USD" } `
        @{ "Idempotency-Key" = "$run-deposit"; traceparent = $traceParent }
    Assert-Status $deposit 201 "deposit (trace $traceId)"

    $transferBody = @{ debitAccountId = $alice.Body.id; creditAccountId = $bob.Body.id; amount = 2500; currency = "USD" }
    $transferKey = @{ "Idempotency-Key" = "$run-transfer" }
    Assert-Status (Send-Json "/transfers" $transferBody $transferKey) 201 "transfer"
    Assert-Status (Send-Json "/transfers" $transferBody $transferKey) 200 "replayed transfer"

    $overdraft = Send-Json "/accounts/$($bob.Body.id)/withdrawals" @{ amount = 999999; currency = "USD" } `
        @{ "Idempotency-Key" = "$run-overdraft" }
    Assert-Status $overdraft 422 "overdraft withdrawal"

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
```

- [ ] **Step 8: Run the smoke test**

Make sure no API is running on port 5289, then run:

```powershell
powershell -ExecutionPolicy Bypass -File observability\smoke-test.ps1
```

Expected: a column of `ok` lines ending with `SMOKE TEST PASSED`. A line starting with `MISSING:` or `FAILED:` names what to fix. For a `MISSING` Prometheus, Tempo or Loki line, read `docker compose logs otel-collector` first: an export error there names the store that refused the data.

- [ ] **Step 9: Confirm every metric name the dashboard uses exists in Prometheus**

Run, right after the smoke test passed:

```powershell
$expected = "ledger_movements_total", "ledger_movements_amount_total", "ledger_account_lock_wait_duration_seconds_bucket",
    "http_server_request_duration_seconds_bucket", "http_server_request_duration_seconds_count",
    "db_client_connection_count", "db_client_operation_duration_seconds_bucket",
    "dotnet_gc_last_collection_heap_size_bytes", "dotnet_gc_collections_total"
$actual = (Invoke-RestMethod "http://localhost:9090/api/v1/label/__name__/values").data
$expected | Where-Object { $actual -notcontains $_ } | ForEach-Object { "MISSING $_" }
$actual | Where-Object { $_ -match "^(db_client|npgsql)" }
```

Expected: no `MISSING` line. The last command lists the PostgreSQL driver metrics as Prometheus named them.

The seven `ledger_`, `http_server_` and `dotnet_gc_` names were confirmed while this plan was written. The two `db_client_` names were not, because that check ran without a database. If either is reported `MISSING`, pick the matching name from the listed driver metrics (the one for open connections, and the `_bucket` series for command duration), and replace it in panels 13 and 14 of `ledger-overview.json`. For panel 13 also run `Invoke-RestMethod "http://localhost:9090/api/v1/series?match[]=<name>"` and use the label and value that mark a connection as in use. Then run `docker compose restart grafana`.

- [ ] **Step 10: Check the dashboard by eye**

Open `http://localhost:3000/d/ledger-overview`. Every panel must show data except the `5xx` series of "Error rate", which stays empty unless a request failed with a 500. If a panel says "No data", copy its query into `http://localhost:9090/graph`, with `$__rate_interval` replaced by `1m`, to see whether the name or a label is wrong, fix `ledger-overview.json`, and run `docker compose restart grafana`.

Then open one trace: in Grafana choose Explore, the Tempo data source, and paste the trace id the smoke test printed on its `deposit (trace ...)` line. The trace must show the request span with `ledger.movement.kind`, `ledger.movement.outcome` and `ledger.transfer.id`, and database spans below it. Its "Logs for this span" link must open the movement log line in Loki.

- [ ] **Step 11: Update `README.md`**

Replace the paragraph that starts `The API listens on` with:

```markdown
The API listens on `http://localhost:5289`. Amounts are in minor units, so `10000` is 100.00.

`docker compose up -d` also starts the monitoring stack: an OpenTelemetry Collector, Prometheus, Tempo, Loki and Grafana. To start only the database, run `docker compose up -d postgres`; the API then runs as before and drops its telemetry.
```

Add this section directly above `## Run the tests`:

````markdown
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
````

In the "Design notes" section, add this paragraph directly above the `**Known trade-off.**` paragraph:

```markdown
**Telemetry stays out of the money path.** A decorator around the transfer service records each movement's outcome, amount and log line after the real service has returned or thrown, and passes the result or the exception through untouched. The transfer service itself only measures how long its lock query took. The outcome counter sees requests that reach the transfer service; a request refused earlier (missing idempotency key, unknown currency, malformed JSON) shows up as a `400` in the HTTP metrics instead.
```

Replace the last paragraph of the file (under `## Not in this version`) with:

```markdown
Authentication, the Docker image and Kubernetes deployment, and load tests are planned as separate pieces of work.
```

- [ ] **Step 12: Update the tutorial in `CLAUDE.md`**

In the section `### 2. Start the database`, replace the sentence that starts `This starts PostgreSQL 17` with:

```markdown
This starts PostgreSQL 17 on `localhost:5432` with database, user and password all `umutledger`, and the monitoring stack (OpenTelemetry Collector, Prometheus, Tempo, Loki, Grafana). The data is kept in Docker volumes, so it survives a restart. To start only the database, run `docker compose up -d postgres`.
```

Rename the heading `### 11. Stop and clean up` to `### 13. Stop and clean up`, and add these two sections directly above it:

````markdown
### 11. See the telemetry in Grafana
With the containers and the API running, open `http://localhost:3000` (no login) and choose the "Ledger overview" dashboard. Send a few requests (step 6) and the panels fill within about 10 seconds.

To follow one request: every error response has a `traceId`. In Grafana choose Explore, the Tempo data source, and paste the id. The trace shows the request, its SQL commands, and a link to its log lines in Loki.

Prometheus is also reachable directly at `http://localhost:9090`.

### 12. Run the smoke test for the monitoring stack
Stop the API first (the script starts its own copy on port 5289).
```powershell
powershell -ExecutionPolicy Bypass -File observability\smoke-test.ps1
```
It starts the containers and the API, sends a deposit, a transfer, a replay and a rejected withdrawal, then checks that the metrics reached Prometheus, the trace reached Tempo and the log line reached Loki. It ends with `SMOKE TEST PASSED`, or with a `MISSING:` or `FAILED:` line naming what did not arrive.
````

- [ ] **Step 13: Run the whole suite with the coverage gate one last time**

Run: `dotnet test -p:CollectCoverage=true`
Expected: all tests pass; line and branch coverage 100%.

---

## Definition of done

- `dotnet test -p:CollectCoverage=true` passes with 100% line and branch coverage.
- `observability\smoke-test.ps1` ends with `SMOKE TEST PASSED`.
- Every panel of "Ledger overview" shows data after the smoke test (the `5xx` series excepted).
- `README.md` and the `CLAUDE.md` tutorial describe the new steps.
