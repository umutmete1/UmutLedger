# Core Ledger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the core of UmutLedger: a double-entry ledger Web API with accounts, deposits, withdrawals and idempotent transfers on PostgreSQL, tested at 100% coverage.

**Architecture:** One ASP.NET Core project with `Domain`, `Application`, `Infrastructure` and `Api` folders, plus a test project. A self-validating domain holds every money rule; services run each transfer in one database transaction with ordered `SELECT ... FOR UPDATE` row locks and a unique idempotency key; thin controllers translate HTTP, and one exception handler turns domain exceptions into RFC 9457 problem details.

**Tech Stack:** .NET 10 (SDK 10.0.401), ASP.NET Core controllers, EF Core 10 with Npgsql, PostgreSQL 17, xUnit, Testcontainers, `WebApplicationFactory`, `FakeTimeProvider`, Coverlet.

**Spec:** `docs/superpowers/specs/2026-10-11-core-ledger-design.md`

**Execution method:** Subagent-driven (chosen by the owner on 2026-10-11). A fresh subagent implements each task and a fresh reviewer checks it before the next task starts.

## Global Constraints

- Target framework is `net10.0`. EF Core and Npgsql packages must resolve to a 10.x version.
- Money is a `long` in minor units. No `decimal`, `double` or `float` anywhere in the money path.
- Every domain type is self-validating: no public setters, no public parameterless constructor, invalid input throws a `LedgerException`.
- `Domain` references nothing outside itself: no EF Core, no ASP.NET, no Npgsql.
- Controllers hold no business logic and no `try/catch`. They depend on `IAccountService` and `ITransferService`.
- Services use `LedgerDbContext` directly. No repository interfaces.
- Time comes from an injected `TimeProvider`. Nothing calls `DateTime.UtcNow` or `DateTimeOffset.UtcNow`.
- Ids are UUID version 7, created with `Guid.CreateVersion7`.
- Tests use real PostgreSQL through Testcontainers. Nothing database-related is mocked.
- Tests never reset the database and never change an account balance without holding its row lock. Each test creates its own accounts and idempotency keys.
- Line and branch coverage must be 100%. The only exclusions are EF migration files and code marked `GeneratedCode`.
- Clean code and SOLID as defined in `CLAUDE.md`: intention-revealing names, small single-purpose functions, no duplication, no dead code, no magic numbers, comments only where code cannot explain itself.
- The API project uses block-scoped namespaces (matching the existing files). The test project uses file-scoped namespaces (matching the xUnit template).
- **No git steps.** `C:\UmutLedger` is not a git repository and the owner commits their own work. Never run `git init`, `git add`, `git commit`, or create branches or worktrees.
- Docker Desktop must be running from Task 4 onwards. It is installed but was not running when this plan was written.
- All commands run from `C:\UmutLedger`.

## Review Focus

1. **Amounts large enough to overflow a `long` balance** must be refused with `validation_failed`, never crash with an `OverflowException`. Pinned in Task 1 (`Money`) and Task 2 (`Account`).
2. **A deposit or withdrawal that names the world account itself** must return `system_account_not_allowed`, not `same_account_transfer` and not create money. Pinned in Task 6.
3. **An idempotency key reused on a different endpoint** (a deposit's key sent with a transfer) must return `idempotency_key_reused`. Pinned in Task 6 and Task 9.
4. **Replaying a key after the account can no longer afford the transfer** must still return the original transfer, not `insufficient_funds`. Pinned in Task 6.
5. **A request with an empty body, the wrong content type, or a numeric or unknown currency** must get a 4xx response, never a 500. Pinned in Task 8.

## File Structure

```
C:\UmutLedger\
├── docker-compose.yml                                  Postgres for local development
├── README.md                                           what it is, how to run, design notes
├── src\UmutLedger\
│   ├── Program.cs                                      composition root
│   ├── appsettings.Development.json                    local connection string
│   ├── Domain\
│   │   ├── Require.cs                                  shared guard clauses
│   │   ├── Enums\Currency.cs                           (exists)
│   │   ├── Enums\AccountType.cs
│   │   ├── Exceptions\ErrorKind.cs                     InvalidRequest, NotFound, RuleViolation
│   │   ├── Exceptions\ErrorCodes.cs                    the stable machine-readable codes
│   │   ├── Exceptions\LedgerException.cs               base type: kind + code + message
│   │   ├── Exceptions\*.cs                             one file per concrete exception (9)
│   │   ├── ValueObjects\Money.cs
│   │   ├── ValueObjects\IdempotencyKey.cs
│   │   ├── Entities\Account.cs                         (exists, rewritten)
│   │   ├── Entities\Entry.cs
│   │   └── Entities\Transfer.cs
│   ├── Application\
│   │   ├── Accounts\IAccountService.cs
│   │   ├── Accounts\AccountService.cs
│   │   ├── Accounts\PageRequest.cs
│   │   ├── Accounts\EntryPage.cs
│   │   ├── Transfers\ITransferService.cs
│   │   ├── Transfers\TransferService.cs
│   │   └── Transfers\TransferOutcome.cs
│   ├── Infrastructure\
│   │   ├── Persistence\LedgerDbContext.cs
│   │   ├── Persistence\LedgerConstraints.cs            constraint and index names
│   │   ├── Persistence\DatabaseFailure.cs              "is the database unavailable?"
│   │   ├── Persistence\IdempotencyKeyConflict.cs       "was this a duplicate key?"
│   │   ├── Persistence\Configurations\AccountConfiguration.cs
│   │   ├── Persistence\Configurations\TransferConfiguration.cs
│   │   ├── Persistence\Configurations\EntryConfiguration.cs
│   │   └── Migrations\                                 generated + append-only triggers
│   └── Api\
│       ├── Controllers\AccountsController.cs
│       ├── Controllers\TransfersController.cs
│       ├── Models\*.cs                                 request and response models
│       ├── Errors\LedgerProblem.cs                     builds and writes problem details
│       ├── Errors\LedgerExceptionHandler.cs
│       └── Errors\InvalidModelStateResponse.cs
└── tests\UmutLedger.Tests\
    ├── Domain\                                         MoneyTests, LedgerExceptionTests, AccountTests, EntryTests, IdempotencyKeyTests, TransferTests
    ├── Infrastructure\                                 SchemaTests, PersistenceTests, DatabaseFailureTests, IdempotencyKeyConflictTests
    ├── Application\                                    PageRequestTests, EntryPageTests, AccountServiceTests, TransferServiceTests, TransferConcurrencyTests
    ├── Api\                                            AccountsApiTests, ErrorHandlingApiTests, TransfersApiTests
    └── Support\                                        TestDatabase, TestLedger, LedgerInvariants, LedgerApi, ApiExtensions, ThrowingController
```

---

### Task 1: Test project, domain errors and `Money`

**Files:**
- Create: `tests/UmutLedger.Tests/UmutLedger.Tests.csproj` (from template, then edited)
- Create: `src/UmutLedger/Domain/Require.cs`
- Create: `src/UmutLedger/Domain/Exceptions/ErrorKind.cs`, `ErrorCodes.cs`, `LedgerException.cs`, and nine concrete exception files
- Create: `src/UmutLedger/Domain/ValueObjects/Money.cs`
- Test: `tests/UmutLedger.Tests/Domain/MoneyTests.cs`, `tests/UmutLedger.Tests/Domain/LedgerExceptionTests.cs`

**Interfaces:**
- Consumes: `UmutLedger.Domain.Enums.Currency` (exists: `USD`, `EUR`, `GBP`).
- Produces:
  - `enum ErrorKind { InvalidRequest, NotFound, RuleViolation }`
  - `static class ErrorCodes` with `const string` members: `ValidationFailed`, `IdempotencyKeyRequired`, `SameAccountTransfer`, `AccountNotFound`, `TransferNotFound`, `CurrencyMismatch`, `InsufficientFunds`, `SystemAccountNotAllowed`, `IdempotencyKeyReused`, `ServiceUnavailable`, `InternalError`
  - `abstract class LedgerException : Exception` with `ErrorKind Kind` and `string Code`. The domain stays free of HTTP: each exception carries a kind, and the API maps the kind to a status in Task 8 (`InvalidRequest` 400, `NotFound` 404, `RuleViolation` 422).
  - `ValidationFailedException(string message)`, `IdempotencyKeyRequiredException()`, `SameAccountTransferException()`, `AccountNotFoundException(Guid accountId)`, `TransferNotFoundException(Guid transferId)`, `CurrencyMismatchException(Currency expected, Currency actual)`, `InsufficientFundsException(Guid accountId)`, `SystemAccountNotAllowedException(Guid accountId)`, `IdempotencyKeyReusedException()`
  - `static class Require` with `Guid NotEmpty(Guid value, string description)` and `TEnum Defined<TEnum>(TEnum value) where TEnum : struct, Enum`
  - `sealed record Money(long amount, Currency currency)` with `long Amount`, `Currency Currency`, `Money Add(Money)`, `Money Subtract(Money)`, `bool IsLessThan(Money)`, `void EnsurePositive()`

- [ ] **Step 1: Create the test project**

```powershell
dotnet new xunit -n UmutLedger.Tests -o tests/UmutLedger.Tests --framework net10.0
dotnet sln UmutLedger.slnx add tests/UmutLedger.Tests/UmutLedger.Tests.csproj
dotnet add tests/UmutLedger.Tests reference src/UmutLedger/UmutLedger.csproj
dotnet remove tests/UmutLedger.Tests package coverlet.collector
dotnet add tests/UmutLedger.Tests package coverlet.msbuild
dotnet add tests/UmutLedger.Tests package Testcontainers.PostgreSql
dotnet add tests/UmutLedger.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add tests/UmutLedger.Tests package Microsoft.Extensions.TimeProvider.Testing
Remove-Item tests/UmutLedger.Tests/UnitTest1.cs
```

- [ ] **Step 2: Add the coverage settings to the test project**

Add this `PropertyGroup` to `tests/UmutLedger.Tests/UmutLedger.Tests.csproj`. Coverage is only collected when `-p:CollectCoverage=true` is passed, so single-test runs during development are not failed by the threshold.

```xml
  <PropertyGroup>
    <Include>[UmutLedger]*</Include>
    <ExcludeByFile>**/Migrations/*.cs</ExcludeByFile>
    <ExcludeByAttribute>GeneratedCodeAttribute</ExcludeByAttribute>
    <Threshold>100</Threshold>
    <ThresholdType>line,branch</ThresholdType>
    <ThresholdStat>total</ThresholdStat>
  </PropertyGroup>
```

- [ ] **Step 3: Write the failing tests**

`tests/UmutLedger.Tests/Domain/MoneyTests.cs`:

```csharp
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Domain;

public class MoneyTests
{
    private const Currency UnsupportedCurrency = (Currency)99;

    [Fact]
    public void Constructor_UnsupportedCurrency_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Money(100, UnsupportedCurrency));
    }

    [Fact]
    public void Constructor_ValidValues_ExposesThem()
    {
        var money = new Money(-250, Currency.EUR);

        Assert.Equal(-250, money.Amount);
        Assert.Equal(Currency.EUR, money.Currency);
    }

    [Fact]
    public void Equals_SameAmountAndCurrency_IsTrue()
    {
        Assert.Equal(new Money(100, Currency.USD), new Money(100, Currency.USD));
        Assert.NotEqual(new Money(100, Currency.USD), new Money(100, Currency.EUR));
    }

    [Fact]
    public void Add_SameCurrency_ReturnsSum()
    {
        Assert.Equal(new Money(150, Currency.USD), new Money(100, Currency.USD).Add(new Money(50, Currency.USD)));
    }

    [Fact]
    public void Add_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => new Money(100, Currency.USD).Add(new Money(50, Currency.EUR)));
    }

    [Fact]
    public void Add_ResultExceedsLongRange_ThrowsValidationFailed()
    {
        Assert.Throws<ValidationFailedException>(() => new Money(long.MaxValue, Currency.USD).Add(new Money(1, Currency.USD)));
    }

    [Fact]
    public void Subtract_SameCurrency_ReturnsDifference()
    {
        Assert.Equal(new Money(-50, Currency.USD), new Money(50, Currency.USD).Subtract(new Money(100, Currency.USD)));
    }

    [Fact]
    public void Subtract_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => new Money(100, Currency.USD).Subtract(new Money(50, Currency.GBP)));
    }

    [Fact]
    public void Subtract_ResultExceedsLongRange_ThrowsValidationFailed()
    {
        Assert.Throws<ValidationFailedException>(() => new Money(long.MinValue, Currency.USD).Subtract(new Money(1, Currency.USD)));
    }

    [Fact]
    public void IsLessThan_SameCurrency_ComparesAmounts()
    {
        Assert.True(new Money(99, Currency.USD).IsLessThan(new Money(100, Currency.USD)));
        Assert.False(new Money(100, Currency.USD).IsLessThan(new Money(100, Currency.USD)));
    }

    [Fact]
    public void IsLessThan_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => new Money(1, Currency.USD).IsLessThan(new Money(2, Currency.EUR)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EnsurePositive_ZeroOrNegative_Throws(long amount)
    {
        Assert.Throws<ValidationFailedException>(() => new Money(amount, Currency.USD).EnsurePositive());
    }

    [Fact]
    public void EnsurePositive_Positive_DoesNotThrow()
    {
        new Money(1, Currency.USD).EnsurePositive();
    }
}
```

`tests/UmutLedger.Tests/Domain/LedgerExceptionTests.cs`:

```csharp
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Tests.Domain;

public class LedgerExceptionTests
{
    private static readonly (LedgerException Exception, ErrorKind Kind, string Code)[] Cases =
    [
        (new ValidationFailedException("bad"), ErrorKind.InvalidRequest, "validation_failed"),
        (new IdempotencyKeyRequiredException(), ErrorKind.InvalidRequest, "idempotency_key_required"),
        (new SameAccountTransferException(), ErrorKind.InvalidRequest, "same_account_transfer"),
        (new AccountNotFoundException(Guid.NewGuid()), ErrorKind.NotFound, "account_not_found"),
        (new TransferNotFoundException(Guid.NewGuid()), ErrorKind.NotFound, "transfer_not_found"),
        (new CurrencyMismatchException(Currency.USD, Currency.EUR), ErrorKind.RuleViolation, "currency_mismatch"),
        (new InsufficientFundsException(Guid.NewGuid()), ErrorKind.RuleViolation, "insufficient_funds"),
        (new SystemAccountNotAllowedException(Guid.NewGuid()), ErrorKind.RuleViolation, "system_account_not_allowed"),
        (new IdempotencyKeyReusedException(), ErrorKind.RuleViolation, "idempotency_key_reused"),
    ];

    [Fact]
    public void EveryException_CarriesItsKindCodeAndMessage()
    {
        Assert.All(Cases, testCase =>
        {
            Assert.Equal(testCase.Kind, testCase.Exception.Kind);
            Assert.Equal(testCase.Code, testCase.Exception.Code);
            Assert.False(string.IsNullOrWhiteSpace(testCase.Exception.Message));
        });
    }

    [Fact]
    public void ErrorCodes_InfrastructureCodes_AreStable()
    {
        Assert.Equal("service_unavailable", ErrorCodes.ServiceUnavailable);
        Assert.Equal("internal_error", ErrorCodes.InternalError);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Domain"`
Expected: build FAILS with errors such as `The type or namespace name 'Exceptions' does not exist in the namespace 'UmutLedger.Domain'`.

- [ ] **Step 5: Write the error types**

`src/UmutLedger/Domain/Exceptions/ErrorKind.cs`:

```csharp
namespace UmutLedger.Domain.Exceptions
{
    public enum ErrorKind
    {
        InvalidRequest,
        NotFound,
        RuleViolation
    }
}
```

`src/UmutLedger/Domain/Exceptions/ErrorCodes.cs`:

```csharp
namespace UmutLedger.Domain.Exceptions
{
    public static class ErrorCodes
    {
        public const string ValidationFailed = "validation_failed";
        public const string IdempotencyKeyRequired = "idempotency_key_required";
        public const string SameAccountTransfer = "same_account_transfer";
        public const string AccountNotFound = "account_not_found";
        public const string TransferNotFound = "transfer_not_found";
        public const string CurrencyMismatch = "currency_mismatch";
        public const string InsufficientFunds = "insufficient_funds";
        public const string SystemAccountNotAllowed = "system_account_not_allowed";
        public const string IdempotencyKeyReused = "idempotency_key_reused";
        public const string ServiceUnavailable = "service_unavailable";
        public const string InternalError = "internal_error";
    }
}
```

`src/UmutLedger/Domain/Exceptions/LedgerException.cs`:

```csharp
namespace UmutLedger.Domain.Exceptions
{
    public abstract class LedgerException : Exception
    {
        protected LedgerException(ErrorKind kind, string code, string message) : base(message)
        {
            Kind = kind;
            Code = code;
        }

        public ErrorKind Kind { get; }

        public string Code { get; }
    }
}
```

Create one file per concrete exception in `src/UmutLedger/Domain/Exceptions/`, each named after its class. Each file wraps its class in `namespace UmutLedger.Domain.Exceptions { ... }`. `CurrencyMismatchException.cs` also needs `using UmutLedger.Domain.Enums;`.

```csharp
public sealed class ValidationFailedException(string message)
    : LedgerException(ErrorKind.InvalidRequest, ErrorCodes.ValidationFailed, message);

public sealed class IdempotencyKeyRequiredException()
    : LedgerException(ErrorKind.InvalidRequest, ErrorCodes.IdempotencyKeyRequired, "The Idempotency-Key header is required.");

public sealed class SameAccountTransferException()
    : LedgerException(ErrorKind.InvalidRequest, ErrorCodes.SameAccountTransfer, "The debit and credit accounts must be different.");

public sealed class AccountNotFoundException(Guid accountId)
    : LedgerException(ErrorKind.NotFound, ErrorCodes.AccountNotFound, $"Account {accountId} does not exist.");

public sealed class TransferNotFoundException(Guid transferId)
    : LedgerException(ErrorKind.NotFound, ErrorCodes.TransferNotFound, $"Transfer {transferId} does not exist.");

public sealed class CurrencyMismatchException(Currency expected, Currency actual)
    : LedgerException(ErrorKind.RuleViolation, ErrorCodes.CurrencyMismatch, $"Expected currency {expected} but received {actual}.");

public sealed class InsufficientFundsException(Guid accountId)
    : LedgerException(ErrorKind.RuleViolation, ErrorCodes.InsufficientFunds, $"Account {accountId} does not have enough funds.");

public sealed class SystemAccountNotAllowedException(Guid accountId)
    : LedgerException(ErrorKind.RuleViolation, ErrorCodes.SystemAccountNotAllowed, $"Account {accountId} is a system account and cannot be used in this request.");

public sealed class IdempotencyKeyReusedException()
    : LedgerException(ErrorKind.RuleViolation, ErrorCodes.IdempotencyKeyReused, "The idempotency key was already used for a different request.");
```

- [ ] **Step 6: Write `Require` and `Money`**

`src/UmutLedger/Domain/Require.cs`:

```csharp
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Domain
{
    public static class Require
    {
        public static Guid NotEmpty(Guid value, string description)
        {
            if (value == Guid.Empty)
            {
                throw new ValidationFailedException($"{description} is required.");
            }

            return value;
        }

        public static TEnum Defined<TEnum>(TEnum value) where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(value))
            {
                throw new ValidationFailedException($"{typeof(TEnum).Name} '{value}' is not supported.");
            }

            return value;
        }
    }
}
```

`src/UmutLedger/Domain/ValueObjects/Money.cs`:

```csharp
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Domain.ValueObjects
{
    public sealed record Money
    {
        public Money(long amount, Currency currency)
        {
            Amount = amount;
            Currency = Require.Defined(currency);
        }

        public long Amount { get; }

        public Currency Currency { get; }

        public Money Add(Money other) => Combine(other, static (left, right) => checked(left + right));

        public Money Subtract(Money other) => Combine(other, static (left, right) => checked(left - right));

        public bool IsLessThan(Money other)
        {
            EnsureSameCurrency(other);
            return Amount < other.Amount;
        }

        public void EnsurePositive()
        {
            if (Amount <= 0)
            {
                throw new ValidationFailedException("Amount must be greater than zero.");
            }
        }

        private Money Combine(Money other, Func<long, long, long> operation)
        {
            EnsureSameCurrency(other);
            try
            {
                return new Money(operation(Amount, other.Amount), Currency);
            }
            catch (OverflowException)
            {
                throw new ValidationFailedException("Amount is outside the supported range.");
            }
        }

        private void EnsureSameCurrency(Money other)
        {
            if (other.Currency != Currency)
            {
                throw new CurrencyMismatchException(Currency, other.Currency);
            }
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Domain"`
Expected: PASS, 16 tests. The 7 pre-existing warnings in `Account.cs` remain until Task 2.

---

### Task 2: `Account` entity

**Files:**
- Create: `src/UmutLedger/Domain/Enums/AccountType.cs`
- Modify: `src/UmutLedger/Domain/Entities/Account.cs` (full rewrite)
- Test: `tests/UmutLedger.Tests/Domain/AccountTests.cs`

**Interfaces:**
- Consumes: `Money`, `Require`, `ValidationFailedException`, `InsufficientFundsException`, `CurrencyMismatchException` from Task 1.
- Produces:
  - `enum AccountType { Customer, System }`
  - `sealed class Account` with constructor `Account(Guid id, string name, Currency currency, AccountType type, DateTimeOffset createdAt)`, constant `MaxNameLength = 100`, get-only `Id`, `Name`, `Currency`, `Type`, `CreatedAt`, `long Balance { get; private set; }`, and methods `void Debit(Money amount)`, `void Credit(Money amount)`.

- [ ] **Step 1: Write the failing tests**

`tests/UmutLedger.Tests/Domain/AccountTests.cs`:

```csharp
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Domain;

public class AccountTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static Account NewAccount(AccountType type = AccountType.Customer, string name = "Alice") =>
        new(Guid.CreateVersion7(), name, Currency.USD, type, CreatedAt);

    private static Money Usd(long amount) => new(amount, Currency.USD);

    [Fact]
    public void Constructor_ValidValues_CreatesAccountWithZeroBalance()
    {
        var id = Guid.CreateVersion7();

        var account = new Account(id, "  Alice  ", Currency.GBP, AccountType.Customer, CreatedAt);

        Assert.Equal(id, account.Id);
        Assert.Equal("Alice", account.Name);
        Assert.Equal(Currency.GBP, account.Currency);
        Assert.Equal(AccountType.Customer, account.Type);
        Assert.Equal(0, account.Balance);
        Assert.Equal(CreatedAt, account.CreatedAt);
    }

    [Fact]
    public void Constructor_EmptyId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Account(Guid.Empty, "Alice", Currency.USD, AccountType.Customer, CreatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankName_Throws(string name)
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount(name: name));
    }

    [Fact]
    public void Constructor_NameAtMaxLength_IsAccepted()
    {
        var account = NewAccount(name: new string('a', Account.MaxNameLength));

        Assert.Equal(Account.MaxNameLength, account.Name.Length);
    }

    [Fact]
    public void Constructor_NameOverMaxLength_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount(name: new string('a', Account.MaxNameLength + 1)));
    }

    [Fact]
    public void Constructor_UnsupportedCurrency_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Account(Guid.CreateVersion7(), "Alice", (Currency)99, AccountType.Customer, CreatedAt));
    }

    [Fact]
    public void Constructor_UnsupportedType_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount(type: (AccountType)99));
    }

    [Fact]
    public void Credit_PositiveAmount_IncreasesBalance()
    {
        var account = NewAccount();

        account.Credit(Usd(500));

        Assert.Equal(500, account.Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Credit_NonPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount().Credit(Usd(amount)));
    }

    [Fact]
    public void Credit_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => NewAccount().Credit(new Money(100, Currency.EUR)));
    }

    [Fact]
    public void Credit_BalanceWouldExceedLongRange_ThrowsValidationFailedAndKeepsBalance()
    {
        var account = NewAccount();
        account.Credit(Usd(long.MaxValue));

        Assert.Throws<ValidationFailedException>(() => account.Credit(Usd(1)));
        Assert.Equal(long.MaxValue, account.Balance);
    }

    [Fact]
    public void Debit_CustomerWithEnoughFunds_DecreasesBalance()
    {
        var account = NewAccount();
        account.Credit(Usd(500));

        account.Debit(Usd(500));

        Assert.Equal(0, account.Balance);
    }

    [Fact]
    public void Debit_CustomerWithoutEnoughFunds_ThrowsAndKeepsBalance()
    {
        var account = NewAccount();
        account.Credit(Usd(500));

        Assert.Throws<InsufficientFundsException>(() => account.Debit(Usd(501)));
        Assert.Equal(500, account.Balance);
    }

    [Fact]
    public void Debit_SystemAccount_MayGoNegative()
    {
        var account = NewAccount(AccountType.System);

        account.Debit(Usd(300));

        Assert.Equal(-300, account.Balance);
    }

    [Fact]
    public void Debit_SystemBalanceWouldExceedLongRange_ThrowsValidationFailed()
    {
        var account = NewAccount(AccountType.System);
        account.Debit(Usd(long.MaxValue));
        account.Debit(Usd(1));

        Assert.Throws<ValidationFailedException>(() => account.Debit(Usd(1)));
        Assert.Equal(long.MinValue, account.Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Debit_NonPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount(AccountType.System).Debit(Usd(amount)));
    }

    [Fact]
    public void Debit_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => NewAccount(AccountType.System).Debit(new Money(100, Currency.EUR)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AccountTests"`
Expected: build FAILS with `The type or namespace name 'AccountType' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/UmutLedger/Domain/Enums/AccountType.cs`:

```csharp
namespace UmutLedger.Domain.Enums
{
    public enum AccountType
    {
        Customer,
        System
    }
}
```

Replace the whole of `src/UmutLedger/Domain/Entities/Account.cs`:

```csharp
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Domain.Entities
{
    public sealed class Account
    {
        public const int MaxNameLength = 100;
        private const long OpeningBalance = 0;

        public Account(Guid id, string name, Currency currency, AccountType type, DateTimeOffset createdAt)
        {
            var openingBalance = new Money(OpeningBalance, currency);
            Id = Require.NotEmpty(id, "Account id");
            Name = ValidName(name);
            Currency = openingBalance.Currency;
            Type = Require.Defined(type);
            Balance = openingBalance.Amount;
            CreatedAt = createdAt;
        }

        public Guid Id { get; }

        public string Name { get; }

        public Currency Currency { get; }

        public AccountType Type { get; }

        public long Balance { get; private set; }

        public DateTimeOffset CreatedAt { get; }

        public void Credit(Money amount)
        {
            amount.EnsurePositive();
            Balance = CurrentBalance().Add(amount).Amount;
        }

        public void Debit(Money amount)
        {
            amount.EnsurePositive();
            var balance = CurrentBalance();
            if (Type == AccountType.Customer && balance.IsLessThan(amount))
            {
                throw new InsufficientFundsException(Id);
            }

            Balance = balance.Subtract(amount).Amount;
        }

        private Money CurrentBalance() => new(Balance, Currency);

        private static string ValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ValidationFailedException("Account name is required.");
            }

            var trimmedName = name.Trim();
            if (trimmedName.Length > MaxNameLength)
            {
                throw new ValidationFailedException($"Account name must be at most {MaxNameLength} characters.");
            }

            return trimmedName;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AccountTests"`
Expected: PASS, 20 tests, and the build now has 0 warnings.

---

### Task 3: `IdempotencyKey`, `Entry` and `Transfer`

**Files:**
- Create: `src/UmutLedger/Domain/ValueObjects/IdempotencyKey.cs`
- Create: `src/UmutLedger/Domain/Entities/Entry.cs`
- Create: `src/UmutLedger/Domain/Entities/Transfer.cs`
- Test: `tests/UmutLedger.Tests/Domain/IdempotencyKeyTests.cs`, `EntryTests.cs`, `TransferTests.cs`

**Interfaces:**
- Consumes: `Account` (Task 2), `Money`, `Require`, exceptions (Task 1).
- Produces:
  - `sealed record IdempotencyKey(string? value)` with `string Value` and `const int MaxLength = 255`. A null or blank value throws `IdempotencyKeyRequiredException`; an over-long one throws `ValidationFailedException`.
  - `sealed class Entry(Guid id, Guid transferId, Guid accountId, long amount, DateTimeOffset createdAt)` with get-only properties of the same names.
  - `sealed class Transfer` with get-only `Id`, `IdempotencyKey IdempotencyKey`, `DebitAccountId`, `CreditAccountId`, `long Amount`, `Currency Currency`, `CreatedAt`, `IReadOnlyCollection<Entry> Entries`; factory `static Transfer Execute(Guid id, IdempotencyKey idempotencyKey, Account debitAccount, Account creditAccount, Money amount, DateTimeOffset createdAt)` which debits, credits and returns the transfer with its two entries; and `bool IsSameRequest(Guid debitAccountId, Guid creditAccountId, Money amount)`.
  - When `Transfer.Execute` throws, the two `Account` objects may be partly updated in memory. Callers must discard them and write nothing.

- [ ] **Step 1: Write the failing tests**

`tests/UmutLedger.Tests/Domain/IdempotencyKeyTests.cs`:

```csharp
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Domain;

public class IdempotencyKeyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_MissingValue_ThrowsKeyRequired(string? value)
    {
        Assert.Throws<IdempotencyKeyRequiredException>(() => new IdempotencyKey(value));
    }

    [Fact]
    public void Constructor_ValueAtMaxLength_IsAccepted()
    {
        var value = new string('k', IdempotencyKey.MaxLength);

        Assert.Equal(value, new IdempotencyKey(value).Value);
    }

    [Fact]
    public void Constructor_ValueOverMaxLength_ThrowsValidationFailed()
    {
        Assert.Throws<ValidationFailedException>(() => new IdempotencyKey(new string('k', IdempotencyKey.MaxLength + 1)));
    }

    [Fact]
    public void Equals_SameValue_IsTrue()
    {
        Assert.Equal(new IdempotencyKey("abc"), new IdempotencyKey("abc"));
    }
}
```

`tests/UmutLedger.Tests/Domain/EntryTests.cs`:

```csharp
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Tests.Domain;

public class EntryTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_ValidValues_ExposesThem()
    {
        var id = Guid.CreateVersion7();
        var transferId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();

        var entry = new Entry(id, transferId, accountId, -100, CreatedAt);

        Assert.Equal(id, entry.Id);
        Assert.Equal(transferId, entry.TransferId);
        Assert.Equal(accountId, entry.AccountId);
        Assert.Equal(-100, entry.Amount);
        Assert.Equal(CreatedAt, entry.CreatedAt);
    }

    [Fact]
    public void Constructor_ZeroAmount_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Entry(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 0, CreatedAt));
    }

    [Fact]
    public void Constructor_EmptyId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Entry(Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), 1, CreatedAt));
    }

    [Fact]
    public void Constructor_EmptyTransferId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Entry(Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), 1, CreatedAt));
    }

    [Fact]
    public void Constructor_EmptyAccountId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Entry(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, 1, CreatedAt));
    }
}
```

`tests/UmutLedger.Tests/Domain/TransferTests.cs`:

```csharp
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Domain;

public class TransferTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly IdempotencyKey Key = new("key-1");

    private static Account NewAccount(AccountType type = AccountType.Customer, Currency currency = Currency.USD) =>
        new(Guid.CreateVersion7(), "Account", currency, type, CreatedAt);

    private static Account FundedAccount(long balance)
    {
        var account = NewAccount();
        account.Credit(Usd(balance));
        return account;
    }

    private static Money Usd(long amount) => new(amount, Currency.USD);

    [Fact]
    public void Execute_ValidTransfer_MovesMoneyAndRecordsIt()
    {
        var id = Guid.CreateVersion7();
        var debitAccount = FundedAccount(1000);
        var creditAccount = NewAccount();

        var transfer = Transfer.Execute(id, Key, debitAccount, creditAccount, Usd(400), CreatedAt);

        Assert.Equal(600, debitAccount.Balance);
        Assert.Equal(400, creditAccount.Balance);
        Assert.Equal(id, transfer.Id);
        Assert.Equal(Key, transfer.IdempotencyKey);
        Assert.Equal(debitAccount.Id, transfer.DebitAccountId);
        Assert.Equal(creditAccount.Id, transfer.CreditAccountId);
        Assert.Equal(400, transfer.Amount);
        Assert.Equal(Currency.USD, transfer.Currency);
        Assert.Equal(CreatedAt, transfer.CreatedAt);
    }

    [Fact]
    public void Execute_ValidTransfer_CreatesTwoEntriesThatSumToZero()
    {
        var debitAccount = FundedAccount(1000);
        var creditAccount = NewAccount();

        var transfer = Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, creditAccount, Usd(400), CreatedAt);

        Assert.Equal(2, transfer.Entries.Count);
        Assert.Equal(0, transfer.Entries.Sum(entry => entry.Amount));
        Assert.Equal(-400, transfer.Entries.Single(entry => entry.AccountId == debitAccount.Id).Amount);
        Assert.Equal(400, transfer.Entries.Single(entry => entry.AccountId == creditAccount.Id).Amount);
        Assert.All(transfer.Entries, entry =>
        {
            Assert.Equal(transfer.Id, entry.TransferId);
            Assert.Equal(CreatedAt, entry.CreatedAt);
            Assert.NotEqual(Guid.Empty, entry.Id);
        });
        Assert.Equal(2, transfer.Entries.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public void Execute_EmptyId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => Transfer.Execute(Guid.Empty, Key, FundedAccount(100), NewAccount(), Usd(10), CreatedAt));
    }

    [Fact]
    public void Execute_SameAccountOnBothSides_Throws()
    {
        var account = FundedAccount(100);

        Assert.Throws<SameAccountTransferException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, account, account, Usd(10), CreatedAt));
        Assert.Equal(100, account.Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Execute_NonPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ValidationFailedException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, FundedAccount(100), NewAccount(), Usd(amount), CreatedAt));
    }

    [Fact]
    public void Execute_InsufficientFunds_Throws()
    {
        Assert.Throws<InsufficientFundsException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, FundedAccount(100), NewAccount(), Usd(101), CreatedAt));
    }

    [Fact]
    public void Execute_DebitAccountInAnotherCurrency_Throws()
    {
        var debitAccount = NewAccount(AccountType.System, Currency.EUR);

        Assert.Throws<CurrencyMismatchException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, NewAccount(), Usd(10), CreatedAt));
    }

    [Fact]
    public void Execute_CreditAccountInAnotherCurrency_Throws()
    {
        var creditAccount = NewAccount(currency: Currency.EUR);

        Assert.Throws<CurrencyMismatchException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, FundedAccount(100), creditAccount, Usd(10), CreatedAt));
    }

    [Fact]
    public void IsSameRequest_IdenticalRequest_IsTrue()
    {
        var debitAccount = FundedAccount(100);
        var creditAccount = NewAccount();
        var transfer = Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, creditAccount, Usd(10), CreatedAt);

        Assert.True(transfer.IsSameRequest(debitAccount.Id, creditAccount.Id, Usd(10)));
    }

    [Fact]
    public void IsSameRequest_AnyDifference_IsFalse()
    {
        var debitAccount = FundedAccount(100);
        var creditAccount = NewAccount();
        var transfer = Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, creditAccount, Usd(10), CreatedAt);

        Assert.False(transfer.IsSameRequest(Guid.CreateVersion7(), creditAccount.Id, Usd(10)));
        Assert.False(transfer.IsSameRequest(debitAccount.Id, Guid.CreateVersion7(), Usd(10)));
        Assert.False(transfer.IsSameRequest(debitAccount.Id, creditAccount.Id, Usd(11)));
        Assert.False(transfer.IsSameRequest(debitAccount.Id, creditAccount.Id, new Money(10, Currency.EUR)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Domain"`
Expected: build FAILS with `The type or namespace name 'IdempotencyKey' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/UmutLedger/Domain/ValueObjects/IdempotencyKey.cs`:

```csharp
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Domain.ValueObjects
{
    public sealed record IdempotencyKey
    {
        public const int MaxLength = 255;

        public IdempotencyKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new IdempotencyKeyRequiredException();
            }

            if (value.Length > MaxLength)
            {
                throw new ValidationFailedException($"The idempotency key must be at most {MaxLength} characters.");
            }

            Value = value;
        }

        public string Value { get; }
    }
}
```

`src/UmutLedger/Domain/Entities/Entry.cs`:

```csharp
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Domain.Entities
{
    public sealed class Entry
    {
        public Entry(Guid id, Guid transferId, Guid accountId, long amount, DateTimeOffset createdAt)
        {
            if (amount == 0)
            {
                throw new ValidationFailedException("Entry amount must not be zero.");
            }

            Id = Require.NotEmpty(id, "Entry id");
            TransferId = Require.NotEmpty(transferId, "Entry transfer id");
            AccountId = Require.NotEmpty(accountId, "Entry account id");
            Amount = amount;
            CreatedAt = createdAt;
        }

        public Guid Id { get; }

        public Guid TransferId { get; }

        public Guid AccountId { get; }

        public long Amount { get; }

        public DateTimeOffset CreatedAt { get; }
    }
}
```

`src/UmutLedger/Domain/Entities/Transfer.cs`:

```csharp
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Domain.Entities
{
    public sealed class Transfer
    {
        private readonly List<Entry> _entries = [];

        private Transfer(
            Guid id,
            IdempotencyKey idempotencyKey,
            Guid debitAccountId,
            Guid creditAccountId,
            long amount,
            Currency currency,
            DateTimeOffset createdAt)
        {
            if (debitAccountId == creditAccountId)
            {
                throw new SameAccountTransferException();
            }

            var transferred = new Money(amount, currency);
            transferred.EnsurePositive();

            Id = Require.NotEmpty(id, "Transfer id");
            IdempotencyKey = idempotencyKey;
            DebitAccountId = debitAccountId;
            CreditAccountId = creditAccountId;
            Amount = transferred.Amount;
            Currency = transferred.Currency;
            CreatedAt = createdAt;
        }

        public Guid Id { get; }

        public IdempotencyKey IdempotencyKey { get; }

        public Guid DebitAccountId { get; }

        public Guid CreditAccountId { get; }

        public long Amount { get; }

        public Currency Currency { get; }

        public DateTimeOffset CreatedAt { get; }

        public IReadOnlyCollection<Entry> Entries => _entries;

        public static Transfer Execute(
            Guid id,
            IdempotencyKey idempotencyKey,
            Account debitAccount,
            Account creditAccount,
            Money amount,
            DateTimeOffset createdAt)
        {
            var transfer = new Transfer(id, idempotencyKey, debitAccount.Id, creditAccount.Id, amount.Amount, amount.Currency, createdAt);
            debitAccount.Debit(amount);
            creditAccount.Credit(amount);
            transfer.AddEntry(debitAccount.Id, -amount.Amount);
            transfer.AddEntry(creditAccount.Id, amount.Amount);
            return transfer;
        }

        public bool IsSameRequest(Guid debitAccountId, Guid creditAccountId, Money amount) =>
            DebitAccountId == debitAccountId
            && CreditAccountId == creditAccountId
            && new Money(Amount, Currency) == amount;

        private void AddEntry(Guid accountId, long signedAmount) =>
            _entries.Add(new Entry(Guid.CreateVersion7(CreatedAt), Id, accountId, signedAmount, CreatedAt));
    }
}
```

The private constructor is the one EF Core binds to when it loads a transfer (Task 4), so it takes column-shaped parameters and re-checks the invariants on load.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Domain"`
Expected: PASS, 58 tests in total across the domain test classes.

---

### Task 4: Persistence, migrations and schema rules

**Prerequisite:** start Docker Desktop and wait until `docker info` succeeds.

**Files:**
- Create: `docker-compose.yml`
- Modify: `src/UmutLedger/appsettings.Development.json`
- Modify: `src/UmutLedger/Program.cs` (register the context)
- Create: `src/UmutLedger/Infrastructure/Persistence/LedgerDbContext.cs`, `LedgerConstraints.cs`
- Create: `src/UmutLedger/Infrastructure/Persistence/Configurations/AccountConfiguration.cs`, `TransferConfiguration.cs`, `EntryConfiguration.cs`
- Create: `src/UmutLedger/Infrastructure/Migrations/*` (generated, then one edited)
- Create: `tests/UmutLedger.Tests/Support/TestDatabase.cs`, `TestLedger.cs`, `LedgerInvariants.cs`
- Test: `tests/UmutLedger.Tests/Infrastructure/SchemaTests.cs`, `PersistenceTests.cs`

**Interfaces:**
- Consumes: `Account`, `Transfer`, `Entry`, `Money`, `IdempotencyKey`, enums (Tasks 1 to 3).
- Produces:
  - `sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext` with `DbSet<Account> Accounts`, `DbSet<Transfer> Transfers`, `DbSet<Entry> Entries`.
  - `static class LedgerConstraints` with `const string TransferIdempotencyKey = "ux_transfers_idempotency_key"` (the unique index a duplicate key violates) and `const string ConnectionStringName = "Ledger"`.
  - Tables `accounts`, `transfers`, `entries` with snake_case columns exactly as in spec section 5.
  - Test support: `TestDatabase.GetConnectionStringAsync()`, `TestDatabase.CreateContextAsync()`; `TestLedger.BaseTime`, `TestLedger.Usd(long)`, `TestLedger.NewKey()`, `TestLedger.CreateCustomerAsync(Currency = USD)` returning the new id, `TestLedger.CreateFundedCustomerAsync(long balance, Currency = USD)` returning the id, `TestLedger.DepositAsync(Guid accountId, Money amount, DateTimeOffset at)` returning the `Transfer`, `TestLedger.LoadAccountAsync(Guid)`, `TestLedger.BalanceOfAsync(Guid)`, `TestLedger.WorldAccountIdAsync(Currency)`; `LedgerInvariants.AssertHoldAsync()`.

- [ ] **Step 1: Add packages, the EF tool and local Postgres**

```powershell
dotnet add src/UmutLedger package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/UmutLedger package Microsoft.EntityFrameworkCore.Design
dotnet new tool-manifest
dotnet tool install dotnet-ef
```

Open `src/UmutLedger/UmutLedger.csproj` and confirm both new packages resolved to a 10.x version. If either is higher, re-run its command with `--version 10.0.*`.

Create `docker-compose.yml`:

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

volumes:
  umutledger-postgres:
```

Replace `src/UmutLedger/appsettings.Development.json` with (keep any existing `Logging` section as it is):

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
  }
}
```

- [ ] **Step 2: Write the test support code**

`tests/UmutLedger.Tests/Support/TestDatabase.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Tests.Support;

public static class TestDatabase
{
    private const string PostgresImage = "postgres:17-alpine";

    private static readonly Lazy<Task<string>> MigratedDatabase = new(StartAndMigrateAsync);

    public static Task<string> GetConnectionStringAsync() => MigratedDatabase.Value;

    public static async Task<LedgerDbContext> CreateContextAsync() =>
        new(OptionsFor(await GetConnectionStringAsync()));

    private static async Task<string> StartAndMigrateAsync()
    {
        var container = new PostgreSqlBuilder(PostgresImage).Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();

        await using var db = new LedgerDbContext(OptionsFor(connectionString));
        await db.Database.MigrateAsync();
        return connectionString;
    }

    private static DbContextOptions<LedgerDbContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options;
}
```

One container serves the whole test run. Testcontainers removes it when the test process exits.

`tests/UmutLedger.Tests/Support/TestLedger.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Support;

public static class TestLedger
{
    public static readonly DateTimeOffset BaseTime = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public static Money Usd(long amount) => new(amount, Currency.USD);

    public static IdempotencyKey NewKey() => new(Guid.NewGuid().ToString());

    public static async Task<Guid> CreateCustomerAsync(Currency currency = Currency.USD)
    {
        var account = new Account(Guid.CreateVersion7(), "Test customer", currency, AccountType.Customer, BaseTime);
        await using var db = await TestDatabase.CreateContextAsync();
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    public static async Task<Guid> CreateFundedCustomerAsync(long balance, Currency currency = Currency.USD)
    {
        var accountId = await CreateCustomerAsync(currency);
        await DepositAsync(accountId, new Money(balance, currency), BaseTime);
        return accountId;
    }

    // Seeds a deposit without the service under test, holding both row locks so parallel tests never lose an update.
    public static async Task<Transfer> DepositAsync(Guid accountId, Money amount, DateTimeOffset at)
    {
        var worldAccountId = await WorldAccountIdAsync(amount.Currency);
        Guid[] accountIds = [worldAccountId, accountId];

        await using var db = await TestDatabase.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var accounts = await db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({accountIds}) ORDER BY id FOR UPDATE")
            .ToListAsync();
        var transfer = Transfer.Execute(
            Guid.CreateVersion7(at),
            NewKey(),
            accounts.Single(account => account.Id == worldAccountId),
            accounts.Single(account => account.Id == accountId),
            amount,
            at);
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return transfer;
    }

    public static async Task<Account> LoadAccountAsync(Guid accountId)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        return await db.Accounts.AsNoTracking().SingleAsync(account => account.Id == accountId);
    }

    public static async Task<long> BalanceOfAsync(Guid accountId) => (await LoadAccountAsync(accountId)).Balance;

    public static async Task<Guid> WorldAccountIdAsync(Currency currency)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        return await db.Accounts
            .Where(account => account.Type == AccountType.System && account.Currency == currency)
            .Select(account => account.Id)
            .SingleAsync();
    }
}
```

`tests/UmutLedger.Tests/Support/LedgerInvariants.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Tests.Support;

public static class LedgerInvariants
{
    private const string CurrenciesWhoseEntriesDoNotSumToZero = """
        SELECT count(*)::int AS "Value" FROM (
            SELECT a.currency
            FROM entries e JOIN accounts a ON a.id = e.account_id
            GROUP BY a.currency
            HAVING sum(e.amount) <> 0) unbalanced
        """;

    private const string AccountsWhoseBalanceDiffersFromTheirEntries = """
        SELECT count(*)::int AS "Value"
        FROM accounts a
        WHERE a.balance <> (SELECT coalesce(sum(e.amount), 0) FROM entries e WHERE e.account_id = a.id)
        """;

    private const string TransfersWithoutTwoBalancedEntries = """
        SELECT count(*)::int AS "Value"
        FROM transfers t
        WHERE (SELECT count(*) FROM entries e WHERE e.transfer_id = t.id) <> 2
           OR (SELECT coalesce(sum(e.amount), 0) FROM entries e WHERE e.transfer_id = t.id) <> 0
        """;

    public static async Task AssertHoldAsync()
    {
        await using var db = await TestDatabase.CreateContextAsync();
        Assert.Equal(0, await CountAsync(db, CurrenciesWhoseEntriesDoNotSumToZero));
        Assert.Equal(0, await CountAsync(db, AccountsWhoseBalanceDiffersFromTheirEntries));
        Assert.Equal(0, await CountAsync(db, TransfersWithoutTwoBalancedEntries));
    }

    private static Task<int> CountAsync(LedgerDbContext db, string sql) =>
        db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
```

Each query is one statement, so it reads one consistent snapshot even while other tests commit in parallel.

- [ ] **Step 3: Write the failing tests**

`tests/UmutLedger.Tests/Infrastructure/SchemaTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UmutLedger.Domain.Enums;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Infrastructure;

public class SchemaTests
{
    private const string AppendOnlyViolation = "23001";

    [Fact]
    public async Task Migrations_CreateOneSystemAccountPerCurrency()
    {
        await using var db = await TestDatabase.CreateContextAsync();

        var systemCurrencies = await db.Accounts
            .Where(account => account.Type == AccountType.System)
            .Select(account => account.Currency)
            .ToListAsync();

        Assert.Equal(Enum.GetValues<Currency>().Order(), systemCurrencies.Order());
    }

    [Fact]
    public async Task Accounts_SecondSystemAccountForACurrency_IsRefused()
    {
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO accounts (id, name, currency, type, balance, created_at) VALUES ({Guid.CreateVersion7()}, 'Second world', 'USD', 'System', 0, now())"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("'Overdrawn', 'USD', 'Customer', -1")]
    [InlineData("'   ', 'USD', 'Customer', 0")]
    [InlineData("'Yen account', 'JPY', 'Customer', 0")]
    [InlineData("'Odd type', 'USD', 'Other', 0")]
    public async Task Accounts_InvalidRow_IsRefused(string nameCurrencyTypeBalance)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        var sql = $"INSERT INTO accounts (id, name, currency, type, balance, created_at) VALUES ('{Guid.CreateVersion7()}', {nameCurrencyTypeBalance}, now())";

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Transfers_ZeroAmount_IsRefused()
    {
        var debitAccountId = await TestLedger.CreateCustomerAsync();
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO transfers (id, idempotency_key, debit_account_id, credit_account_id, amount, currency, created_at) VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid().ToString()}, {debitAccountId}, {creditAccountId}, 0, 'USD', now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Transfers_SameAccountOnBothSides_IsRefused()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO transfers (id, idempotency_key, debit_account_id, credit_account_id, amount, currency, created_at) VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid().ToString()}, {accountId}, {accountId}, 10, 'USD', now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Transfers_DuplicateIdempotencyKey_IsRefused()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var existing = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO transfers (id, idempotency_key, debit_account_id, credit_account_id, amount, currency, created_at) VALUES ({Guid.CreateVersion7()}, {existing.IdempotencyKey.Value}, {existing.DebitAccountId}, {existing.CreditAccountId}, 10, 'USD', now())"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_transfers_idempotency_key", exception.ConstraintName);
    }

    [Fact]
    public async Task Entries_ZeroAmount_IsRefused()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO entries (id, transfer_id, account_id, amount, created_at) VALUES ({Guid.CreateVersion7()}, {transfer.Id}, {accountId}, 0, now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("UPDATE transfers SET amount = amount + 1 WHERE id = {0}")]
    [InlineData("DELETE FROM transfers WHERE id = {0}")]
    [InlineData("UPDATE entries SET amount = amount + 1 WHERE transfer_id = {0}")]
    [InlineData("DELETE FROM entries WHERE transfer_id = {0}")]
    public async Task TransfersAndEntries_UpdateOrDelete_IsRefused(string sql)
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, transfer.Id));

        Assert.Equal(AppendOnlyViolation, exception.SqlState);
        await LedgerInvariants.AssertHoldAsync();
    }
}
```

`tests/UmutLedger.Tests/Infrastructure/PersistenceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using UmutLedger.Domain.Enums;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Infrastructure;

public class PersistenceTests
{
    [Fact]
    public async Task Account_SavedAndReloaded_KeepsItsValues()
    {
        var accountId = await TestLedger.CreateCustomerAsync(Currency.GBP);

        var reloaded = await TestLedger.LoadAccountAsync(accountId);

        Assert.Equal("Test customer", reloaded.Name);
        Assert.Equal(Currency.GBP, reloaded.Currency);
        Assert.Equal(AccountType.Customer, reloaded.Type);
        Assert.Equal(0, reloaded.Balance);
        Assert.Equal(TestLedger.BaseTime, reloaded.CreatedAt);
    }

    [Fact]
    public async Task Transfer_SavedAndReloaded_KeepsItsValuesAndEntries()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var saved = await TestLedger.DepositAsync(accountId, TestLedger.Usd(250), TestLedger.BaseTime);

        await using var db = await TestDatabase.CreateContextAsync();
        var reloaded = await db.Transfers.AsNoTracking()
            .Include(transfer => transfer.Entries)
            .SingleAsync(transfer => transfer.IdempotencyKey == saved.IdempotencyKey);

        Assert.Equal(saved.Id, reloaded.Id);
        Assert.Equal(saved.DebitAccountId, reloaded.DebitAccountId);
        Assert.Equal(accountId, reloaded.CreditAccountId);
        Assert.Equal(250, reloaded.Amount);
        Assert.Equal(Currency.USD, reloaded.Currency);
        Assert.Equal(TestLedger.BaseTime, reloaded.CreatedAt);
        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Equal(0, reloaded.Entries.Sum(entry => entry.Amount));
    }

    [Fact]
    public async Task Deposit_UpdatesTheStoredBalance()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(750);

        Assert.Equal(750, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Infrastructure"`
Expected: build FAILS with `The type or namespace name 'Infrastructure' does not exist in the namespace 'UmutLedger'`.

- [ ] **Step 5: Write the context and its configuration**

`src/UmutLedger/Infrastructure/Persistence/LedgerConstraints.cs`:

```csharp
namespace UmutLedger.Infrastructure.Persistence
{
    public static class LedgerConstraints
    {
        public const string ConnectionStringName = "Ledger";
        public const string TransferIdempotencyKey = "ux_transfers_idempotency_key";
    }
}
```

`src/UmutLedger/Infrastructure/Persistence/LedgerDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using UmutLedger.Domain.Entities;

namespace UmutLedger.Infrastructure.Persistence
{
    public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
    {
        public DbSet<Account> Accounts => Set<Account>();

        public DbSet<Transfer> Transfers => Set<Transfer>();

        public DbSet<Entry> Entries => Set<Entry>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
    }
}
```

`src/UmutLedger/Infrastructure/Persistence/Configurations/AccountConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;

namespace UmutLedger.Infrastructure.Persistence.Configurations
{
    public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
    {
        private static readonly DateTimeOffset SeededAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Configure(EntityTypeBuilder<Account> builder)
        {
            builder.ToTable("accounts", table =>
            {
                table.HasCheckConstraint("ck_accounts_customer_balance_non_negative", "type = 'System' OR balance >= 0");
                table.HasCheckConstraint("ck_accounts_name_not_empty", "length(trim(name)) > 0");
                table.HasCheckConstraint("ck_accounts_currency", "currency IN ('USD', 'EUR', 'GBP')");
                table.HasCheckConstraint("ck_accounts_type", "type IN ('Customer', 'System')");
            });

            builder.HasKey(account => account.Id).HasName("pk_accounts");
            builder.Property(account => account.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(account => account.Name).HasColumnName("name");
            builder.Property(account => account.Currency).HasColumnName("currency").HasConversion<string>();
            builder.Property(account => account.Type).HasColumnName("type").HasConversion<string>();
            builder.Property(account => account.Balance).HasColumnName("balance");
            builder.Property(account => account.CreatedAt).HasColumnName("created_at");

            builder.HasIndex(account => account.Currency)
                .IsUnique()
                .HasFilter("type = 'System'")
                .HasDatabaseName("ux_accounts_system_currency");

            builder.HasData(
                WorldAccount("00000000-0000-7000-8000-000000000001", Currency.USD),
                WorldAccount("00000000-0000-7000-8000-000000000002", Currency.EUR),
                WorldAccount("00000000-0000-7000-8000-000000000003", Currency.GBP));
        }

        private static object WorldAccount(string id, Currency currency) => new
        {
            Id = Guid.Parse(id),
            Name = $"World {currency}",
            Currency = currency,
            Type = AccountType.System,
            Balance = 0L,
            CreatedAt = SeededAt
        };
    }
}
```

`src/UmutLedger/Infrastructure/Persistence/Configurations/TransferConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Infrastructure.Persistence.Configurations
{
    public sealed class TransferConfiguration : IEntityTypeConfiguration<Transfer>
    {
        public void Configure(EntityTypeBuilder<Transfer> builder)
        {
            builder.ToTable("transfers", table =>
            {
                table.HasCheckConstraint("ck_transfers_amount_positive", "amount > 0");
                table.HasCheckConstraint("ck_transfers_distinct_accounts", "debit_account_id <> credit_account_id");
                table.HasCheckConstraint("ck_transfers_currency", "currency IN ('USD', 'EUR', 'GBP')");
            });

            builder.HasKey(transfer => transfer.Id).HasName("pk_transfers");
            builder.Property(transfer => transfer.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(transfer => transfer.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .HasConversion(key => key.Value, value => new IdempotencyKey(value));
            builder.Property(transfer => transfer.DebitAccountId).HasColumnName("debit_account_id");
            builder.Property(transfer => transfer.CreditAccountId).HasColumnName("credit_account_id");
            builder.Property(transfer => transfer.Amount).HasColumnName("amount");
            builder.Property(transfer => transfer.Currency).HasColumnName("currency").HasConversion<string>();
            builder.Property(transfer => transfer.CreatedAt).HasColumnName("created_at");

            builder.HasIndex(transfer => transfer.IdempotencyKey)
                .IsUnique()
                .HasDatabaseName(LedgerConstraints.TransferIdempotencyKey);

            builder.HasOne<Account>().WithMany()
                .HasForeignKey(transfer => transfer.DebitAccountId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_transfers_debit_account");
            builder.HasOne<Account>().WithMany()
                .HasForeignKey(transfer => transfer.CreditAccountId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_transfers_credit_account");
            builder.HasMany(transfer => transfer.Entries).WithOne()
                .HasForeignKey(entry => entry.TransferId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_entries_transfer");
        }
    }
}
```

`src/UmutLedger/Infrastructure/Persistence/Configurations/EntryConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UmutLedger.Domain.Entities;

namespace UmutLedger.Infrastructure.Persistence.Configurations
{
    public sealed class EntryConfiguration : IEntityTypeConfiguration<Entry>
    {
        public void Configure(EntityTypeBuilder<Entry> builder)
        {
            builder.ToTable("entries", table =>
                table.HasCheckConstraint("ck_entries_amount_not_zero", "amount <> 0"));

            builder.HasKey(entry => entry.Id).HasName("pk_entries");
            builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(entry => entry.TransferId).HasColumnName("transfer_id");
            builder.Property(entry => entry.AccountId).HasColumnName("account_id");
            builder.Property(entry => entry.Amount).HasColumnName("amount");
            builder.Property(entry => entry.CreatedAt).HasColumnName("created_at");

            builder.HasIndex(entry => new { entry.AccountId, entry.Id }).HasDatabaseName("ix_entries_account_id_id");

            builder.HasOne<Account>().WithMany()
                .HasForeignKey(entry => entry.AccountId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_entries_account");
        }
    }
}
```

- [ ] **Step 6: Register the context in `Program.cs`**

Add these usings at the top of `src/UmutLedger/Program.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using UmutLedger.Infrastructure.Persistence;
```

Add this registration directly after `builder.Services.AddOpenApi();`. The connection string is read when the context is first resolved, not at startup, so tests can override configuration:

```csharp
builder.Services.AddDbContext<LedgerDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString(LedgerConstraints.ConnectionStringName)));
```

- [ ] **Step 7: Generate the migrations**

```powershell
dotnet ef migrations add InitialCreate --project src/UmutLedger --output-dir Infrastructure/Migrations
dotnet ef migrations add AppendOnlyLedger --project src/UmutLedger --output-dir Infrastructure/Migrations
```

Open the generated `*_InitialCreate.cs` and confirm it creates `accounts`, `transfers` and `entries`, the four `ck_accounts_*` constraints, both unique indexes, and an `InsertData` call with the three world accounts. Do not edit it.

The second migration is generated empty. Replace the bodies of `Up` and `Down` in `*_AppendOnlyLedger.cs`:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION reject_ledger_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER transfers_append_only BEFORE UPDATE OR DELETE ON transfers
                    FOR EACH ROW EXECUTE FUNCTION reject_ledger_mutation();

                CREATE TRIGGER entries_append_only BEFORE UPDATE OR DELETE ON entries
                    FOR EACH ROW EXECUTE FUNCTION reject_ledger_mutation();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER entries_append_only ON entries;
                DROP TRIGGER transfers_append_only ON transfers;
                DROP FUNCTION reject_ledger_mutation();
                """);
        }
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Infrastructure"`
Expected: PASS, 17 tests. The first run pulls `postgres:17-alpine` and can take a minute.

If a test fails with `Docker is either not running or misconfigured`, start Docker Desktop and re-run.

---

### Task 5: `AccountService`

**Files:**
- Create: `src/UmutLedger/Application/Accounts/IAccountService.cs`, `AccountService.cs`, `PageRequest.cs`, `EntryPage.cs`
- Test: `tests/UmutLedger.Tests/Application/PageRequestTests.cs`, `EntryPageTests.cs`, `AccountServiceTests.cs`

**Interfaces:**
- Consumes: `LedgerDbContext` (Task 4), `Account`, `Entry`, exceptions, `TestLedger`, `TestDatabase`.
- Produces:
  - `sealed class PageRequest(int? limit, Guid? cursor)` with `int Limit`, `Guid? Cursor`, `const int DefaultLimit = 50`, `const int MaxLimit = 200`. A limit outside 1 to 200 throws `ValidationFailedException`.
  - `sealed class EntryPage` with `IReadOnlyList<Entry> Items`, `Guid? NextCursor`, and `static EntryPage From(IReadOnlyList<Entry> fetched, int limit)`.
  - `interface IAccountService` with `Task<Account> CreateAsync(string name, Currency currency, CancellationToken cancellationToken)`, `Task<Account> GetAsync(Guid accountId, CancellationToken cancellationToken)`, `Task<EntryPage> GetEntriesAsync(Guid accountId, PageRequest page, CancellationToken cancellationToken)`.
  - `sealed class AccountService(LedgerDbContext db, TimeProvider timeProvider) : IAccountService`.

- [ ] **Step 1: Write the failing tests**

`tests/UmutLedger.Tests/Application/PageRequestTests.cs`:

```csharp
using UmutLedger.Application.Accounts;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Tests.Application;

public class PageRequestTests
{
    [Fact]
    public void Constructor_NoLimit_UsesTheDefault()
    {
        var page = new PageRequest(null, null);

        Assert.Equal(PageRequest.DefaultLimit, page.Limit);
        Assert.Null(page.Cursor);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(PageRequest.MaxLimit)]
    public void Constructor_LimitInRange_IsAccepted(int limit)
    {
        var cursor = Guid.CreateVersion7();

        var page = new PageRequest(limit, cursor);

        Assert.Equal(limit, page.Limit);
        Assert.Equal(cursor, page.Cursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PageRequest.MaxLimit + 1)]
    public void Constructor_LimitOutOfRange_Throws(int limit)
    {
        Assert.Throws<ValidationFailedException>(() => new PageRequest(limit, null));
    }
}
```

`tests/UmutLedger.Tests/Application/EntryPageTests.cs`:

```csharp
using UmutLedger.Application.Accounts;
using UmutLedger.Domain.Entities;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class EntryPageTests
{
    private static List<Entry> Entries(int count) =>
        Enumerable.Range(1, count)
            .Select(amount => new Entry(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), amount, TestLedger.BaseTime))
            .ToList();

    [Fact]
    public void From_MoreFetchedThanLimit_TrimsAndPointsAtTheLastItem()
    {
        var fetched = Entries(3);

        var page = EntryPage.From(fetched, limit: 2);

        Assert.Equal(fetched.Take(2), page.Items);
        Assert.Equal(fetched[1].Id, page.NextCursor);
    }

    [Fact]
    public void From_FetchedEqualsLimit_HasNoNextCursor()
    {
        var fetched = Entries(2);

        var page = EntryPage.From(fetched, limit: 2);

        Assert.Equal(fetched, page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void From_NothingFetched_IsEmpty()
    {
        var page = EntryPage.From([], limit: 2);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }
}
```

`tests/UmutLedger.Tests/Application/AccountServiceTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using UmutLedger.Application.Accounts;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class AccountServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<TResult> WithServiceAsync<TResult>(Func<IAccountService, Task<TResult>> action, TimeProvider? timeProvider = null)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        return await action(new AccountService(db, timeProvider ?? TimeProvider.System));
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_StoresACustomerAccountWithZeroBalance()
    {
        var clock = new FakeTimeProvider(TestLedger.BaseTime);

        var created = await WithServiceAsync(service => service.CreateAsync("Alice", Currency.EUR, None), clock);

        var stored = await TestLedger.LoadAccountAsync(created.Id);
        Assert.Equal("Alice", stored.Name);
        Assert.Equal(Currency.EUR, stored.Currency);
        Assert.Equal(AccountType.Customer, stored.Type);
        Assert.Equal(0, stored.Balance);
        Assert.Equal(TestLedger.BaseTime, stored.CreatedAt);
    }

    [Fact]
    public async Task CreateAsync_BlankName_Throws()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => WithServiceAsync(service => service.CreateAsync(" ", Currency.USD, None)));
    }

    [Fact]
    public async Task GetAsync_ExistingAccount_ReturnsIt()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(300);

        var account = await WithServiceAsync(service => service.GetAsync(accountId, None));

        Assert.Equal(accountId, account.Id);
        Assert.Equal(300, account.Balance);
    }

    [Fact]
    public async Task GetAsync_UnknownAccount_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() => WithServiceAsync(service => service.GetAsync(Guid.CreateVersion7(), None)));
    }

    [Fact]
    public async Task GetEntriesAsync_UnknownAccount_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            WithServiceAsync(service => service.GetEntriesAsync(Guid.CreateVersion7(), new PageRequest(null, null), None)));
    }

    [Fact]
    public async Task GetEntriesAsync_AccountWithoutEntries_ReturnsAnEmptyPage()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var page = await WithServiceAsync(service => service.GetEntriesAsync(accountId, new PageRequest(null, null), None));

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task GetEntriesAsync_SeveralEntries_PagesNewestFirst()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime.AddSeconds(1));
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(200), TestLedger.BaseTime.AddSeconds(2));
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(300), TestLedger.BaseTime.AddSeconds(3));

        var firstPage = await WithServiceAsync(service => service.GetEntriesAsync(accountId, new PageRequest(2, null), None));
        var secondPage = await WithServiceAsync(service => service.GetEntriesAsync(accountId, new PageRequest(2, firstPage.NextCursor), None));

        Assert.Equal([300, 200], firstPage.Items.Select(entry => entry.Amount));
        Assert.Equal(firstPage.Items[^1].Id, firstPage.NextCursor);
        Assert.Equal([100], secondPage.Items.Select(entry => entry.Amount));
        Assert.Null(secondPage.NextCursor);
    }

    [Fact]
    public async Task GetEntriesAsync_OnlyReturnsEntriesOfTheRequestedAccount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        await TestLedger.CreateFundedCustomerAsync(999);

        var page = await WithServiceAsync(service => service.GetEntriesAsync(accountId, new PageRequest(null, null), None));

        Assert.Single(page.Items);
        Assert.All(page.Items, entry => Assert.Equal(accountId, entry.AccountId));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Application"`
Expected: build FAILS with `The type or namespace name 'Application' does not exist in the namespace 'UmutLedger'`.

- [ ] **Step 3: Write the implementation**

`src/UmutLedger/Application/Accounts/PageRequest.cs`:

```csharp
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Application.Accounts
{
    public sealed class PageRequest
    {
        public const int DefaultLimit = 50;
        public const int MaxLimit = 200;
        private const int MinLimit = 1;

        public PageRequest(int? limit, Guid? cursor)
        {
            Limit = limit ?? DefaultLimit;
            if (Limit is < MinLimit or > MaxLimit)
            {
                throw new ValidationFailedException($"limit must be between {MinLimit} and {MaxLimit}.");
            }

            Cursor = cursor;
        }

        public int Limit { get; }

        public Guid? Cursor { get; }
    }
}
```

`src/UmutLedger/Application/Accounts/EntryPage.cs`:

```csharp
using UmutLedger.Domain.Entities;

namespace UmutLedger.Application.Accounts
{
    public sealed class EntryPage
    {
        private EntryPage(IReadOnlyList<Entry> items, Guid? nextCursor)
        {
            Items = items;
            NextCursor = nextCursor;
        }

        public IReadOnlyList<Entry> Items { get; }

        public Guid? NextCursor { get; }

        public static EntryPage From(IReadOnlyList<Entry> fetched, int limit)
        {
            var items = fetched.Take(limit).ToList();
            Guid? nextCursor = fetched.Count > limit ? items[^1].Id : null;
            return new EntryPage(items, nextCursor);
        }
    }
}
```

`src/UmutLedger/Application/Accounts/IAccountService.cs`:

```csharp
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;

namespace UmutLedger.Application.Accounts
{
    public interface IAccountService
    {
        Task<Account> CreateAsync(string name, Currency currency, CancellationToken cancellationToken);

        Task<Account> GetAsync(Guid accountId, CancellationToken cancellationToken);

        Task<EntryPage> GetEntriesAsync(Guid accountId, PageRequest page, CancellationToken cancellationToken);
    }
}
```

`src/UmutLedger/Application/Accounts/AccountService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Application.Accounts
{
    public sealed class AccountService(LedgerDbContext db, TimeProvider timeProvider) : IAccountService
    {
        private const int LookAheadForNextPage = 1;

        public async Task<Account> CreateAsync(string name, Currency currency, CancellationToken cancellationToken)
        {
            var now = timeProvider.GetUtcNow();
            var account = new Account(Guid.CreateVersion7(now), name, currency, AccountType.Customer, now);
            db.Accounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);
            return account;
        }

        public async Task<Account> GetAsync(Guid accountId, CancellationToken cancellationToken) =>
            await db.Accounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == accountId, cancellationToken)
            ?? throw new AccountNotFoundException(accountId);

        public async Task<EntryPage> GetEntriesAsync(Guid accountId, PageRequest page, CancellationToken cancellationToken)
        {
            if (!await db.Accounts.AnyAsync(account => account.Id == accountId, cancellationToken))
            {
                throw new AccountNotFoundException(accountId);
            }

            var fetched = await EntriesOlderThan(page.Cursor)
                .AsNoTracking()
                .Where(entry => entry.AccountId == accountId)
                .OrderByDescending(entry => entry.Id)
                .Take(page.Limit + LookAheadForNextPage)
                .ToListAsync(cancellationToken);
            return EntryPage.From(fetched, page.Limit);
        }

        // Entry ids are time-ordered UUIDs, compared by PostgreSQL so the cursor follows the same order as the sort.
        private IQueryable<Entry> EntriesOlderThan(Guid? cursor) =>
            cursor is null
                ? db.Entries
                : db.Entries.FromSql($"SELECT * FROM entries WHERE id < {cursor.Value}");
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Application"`
Expected: PASS, 17 tests.

---

### Task 6: `TransferService` and idempotency

**Files:**
- Create: `src/UmutLedger/Application/Transfers/ITransferService.cs`, `TransferService.cs`, `TransferOutcome.cs`
- Create: `src/UmutLedger/Infrastructure/Persistence/IdempotencyKeyConflict.cs`
- Modify: `tests/UmutLedger.Tests/Support/TestLedger.cs` (add a service runner)
- Test: `tests/UmutLedger.Tests/Infrastructure/IdempotencyKeyConflictTests.cs`, `tests/UmutLedger.Tests/Application/TransferServiceTests.cs`

**Interfaces:**
- Consumes: `LedgerDbContext`, `LedgerConstraints.TransferIdempotencyKey` (Task 4), `Transfer.Execute`, `Transfer.IsSameRequest`, `IdempotencyKey`, `Money`, `Account`, exceptions.
- Produces:
  - `sealed class TransferOutcome(Transfer transfer, bool wasReplayed)` with `Transfer Transfer` and `bool WasReplayed`.
  - `interface ITransferService`:
    - `Task<TransferOutcome> TransferAsync(IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, Money amount, CancellationToken cancellationToken)`
    - `Task<TransferOutcome> DepositAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken)`
    - `Task<TransferOutcome> WithdrawAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken)`
    - `Task<Transfer> GetAsync(Guid transferId, CancellationToken cancellationToken)` (includes `Entries`)
  - `sealed class TransferService(LedgerDbContext db, TimeProvider timeProvider) : ITransferService`.
  - `static class IdempotencyKeyConflict` with `static bool Matches(DbUpdateException exception)`.
  - Test support: `TestLedger.WithTransferServiceAsync<TResult>(Func<ITransferService, Task<TResult>> action, TimeProvider? timeProvider = null)`.

- [ ] **Step 1: Add the service runner to `TestLedger`**

Add these usings to `tests/UmutLedger.Tests/Support/TestLedger.cs`:

```csharp
using UmutLedger.Application.Transfers;
```

Add this method to the `TestLedger` class. Each call gets its own context, like one HTTP request:

```csharp
    public static async Task<TResult> WithTransferServiceAsync<TResult>(
        Func<ITransferService, Task<TResult>> action,
        TimeProvider? timeProvider = null)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        return await action(new TransferService(db, timeProvider ?? TimeProvider.System));
    }
```

- [ ] **Step 2: Write the failing tests**

`tests/UmutLedger.Tests/Infrastructure/IdempotencyKeyConflictTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Tests.Infrastructure;

public class IdempotencyKeyConflictTests
{
    private static DbUpdateException UpdateFailure(string sqlState, string constraintName) =>
        new("Save failed.", new PostgresException("failure", "ERROR", "ERROR", sqlState, constraintName: constraintName));

    [Fact]
    public void Matches_UniqueViolationOnTheIdempotencyKeyIndex_IsTrue()
    {
        Assert.True(IdempotencyKeyConflict.Matches(UpdateFailure(PostgresErrorCodes.UniqueViolation, LedgerConstraints.TransferIdempotencyKey)));
    }

    [Fact]
    public void Matches_UniqueViolationOnAnotherIndex_IsFalse()
    {
        Assert.False(IdempotencyKeyConflict.Matches(UpdateFailure(PostgresErrorCodes.UniqueViolation, "pk_transfers")));
    }

    [Fact]
    public void Matches_AnotherErrorOnTheIdempotencyKeyIndex_IsFalse()
    {
        Assert.False(IdempotencyKeyConflict.Matches(UpdateFailure(PostgresErrorCodes.CheckViolation, LedgerConstraints.TransferIdempotencyKey)));
    }

    [Fact]
    public void Matches_FailureNotFromPostgres_IsFalse()
    {
        Assert.False(IdempotencyKeyConflict.Matches(new DbUpdateException("Save failed.", new InvalidOperationException())));
    }
}
```

`tests/UmutLedger.Tests/Application/TransferServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class TransferServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Money Usd(long amount) => TestLedger.Usd(amount);

    private static async Task<int> CountTransfersAsync(IdempotencyKey key)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        return await db.Transfers.CountAsync(transfer => transfer.IdempotencyKey == key);
    }

    [Fact]
    public async Task DepositAsync_CreditsTheAccountFromTheWorldAccount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);
        var clock = new FakeTimeProvider(TestLedger.BaseTime);

        var outcome = await TestLedger.WithTransferServiceAsync(service => service.DepositAsync(TestLedger.NewKey(), accountId, Usd(500), None), clock);

        Assert.False(outcome.WasReplayed);
        Assert.Equal(worldAccountId, outcome.Transfer.DebitAccountId);
        Assert.Equal(accountId, outcome.Transfer.CreditAccountId);
        Assert.Equal(TestLedger.BaseTime, outcome.Transfer.CreatedAt);
        Assert.Equal(2, outcome.Transfer.Entries.Count);
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task WithdrawAsync_DebitsTheAccountToTheWorldAccount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(500);
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        var outcome = await TestLedger.WithTransferServiceAsync(service => service.WithdrawAsync(TestLedger.NewKey(), accountId, Usd(200), None));

        Assert.Equal(accountId, outcome.Transfer.DebitAccountId);
        Assert.Equal(worldAccountId, outcome.Transfer.CreditAccountId);
        Assert.Equal(300, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task WithdrawAsync_MoreThanTheBalance_ThrowsAndWritesNothing()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        var key = TestLedger.NewKey();

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.WithdrawAsync(key, accountId, Usd(101), None)));

        Assert.Equal(100, await TestLedger.BalanceOfAsync(accountId));
        Assert.Equal(0, await CountTransfersAsync(key));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_MovesMoneyBetweenTwoCustomers()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var outcome = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, Usd(400), None));

        Assert.False(outcome.WasReplayed);
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(400, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_SameAccountOnBothSides_Throws()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        await Assert.ThrowsAsync<SameAccountTransferException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), accountId, accountId, Usd(10), None)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task TransferAsync_NonPositiveAmount_Throws(long amount)
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, Usd(amount), None)));
    }

    [Fact]
    public async Task TransferAsync_UnknownDebitAccount_Throws()
    {
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), Guid.CreateVersion7(), creditAccountId, Usd(10), None)));
    }

    [Fact]
    public async Task TransferAsync_UnknownCreditAccount_ThrowsAndWritesNothing()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);

        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, Guid.CreateVersion7(), Usd(10), None)));

        Assert.Equal(100, await TestLedger.BalanceOfAsync(debitAccountId));
    }

    [Fact]
    public async Task TransferAsync_RequestCurrencyDiffersFromTheAccounts_ThrowsAndWritesNothing()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100, Currency.EUR);
        var creditAccountId = await TestLedger.CreateCustomerAsync(Currency.EUR);

        await Assert.ThrowsAsync<CurrencyMismatchException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, Usd(10), None)));

        Assert.Equal(100, await TestLedger.BalanceOfAsync(debitAccountId));
    }

    [Fact]
    public async Task TransferAsync_CreditAccountInAnotherCurrency_ThrowsAndWritesNothing()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync(Currency.GBP);

        await Assert.ThrowsAsync<CurrencyMismatchException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, Usd(10), None)));

        Assert.Equal(100, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(0, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task DepositAsync_AccountInAnotherCurrency_Throws()
    {
        var accountId = await TestLedger.CreateCustomerAsync(Currency.EUR);

        await Assert.ThrowsAsync<CurrencyMismatchException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.DepositAsync(TestLedger.NewKey(), accountId, Usd(10), None)));
    }

    [Fact]
    public async Task DepositAsync_UnknownAccount_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.DepositAsync(TestLedger.NewKey(), Guid.CreateVersion7(), Usd(10), None)));
    }

    [Fact]
    public async Task TransferAsync_NamesASystemAccount_Throws()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        await Assert.ThrowsAsync<SystemAccountNotAllowedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), worldAccountId, creditAccountId, Usd(10), None)));

        Assert.Equal(0, await TestLedger.BalanceOfAsync(creditAccountId));
    }

    [Fact]
    public async Task DepositAsync_IntoTheWorldAccountItself_ThrowsSystemAccountNotAllowed()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        await Assert.ThrowsAsync<SystemAccountNotAllowedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.DepositAsync(TestLedger.NewKey(), worldAccountId, Usd(10), None)));
    }

    [Fact]
    public async Task WithdrawAsync_FromTheWorldAccountItself_ThrowsSystemAccountNotAllowed()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        await Assert.ThrowsAsync<SystemAccountNotAllowedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.WithdrawAsync(TestLedger.NewKey(), worldAccountId, Usd(10), None)));
    }

    [Fact]
    public async Task TransferAsync_SameKeyAndSameRequest_ReplaysTheOriginalWithoutWriting()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();

        var original = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(400), None));
        var replay = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(400), None));

        Assert.True(replay.WasReplayed);
        Assert.Equal(original.Transfer.Id, replay.Transfer.Id);
        Assert.Equal(2, replay.Transfer.Entries.Count);
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(1, await CountTransfersAsync(key));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_SameKeyButDifferentAmount_ThrowsKeyReused()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(400), None));

        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(401), None)));

        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
    }

    [Fact]
    public async Task TransferAsync_KeyAlreadyUsedByADeposit_ThrowsKeyReused()
    {
        var debitAccountId = await TestLedger.CreateCustomerAsync();
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await TestLedger.WithTransferServiceAsync(service => service.DepositAsync(key, debitAccountId, Usd(500), None));

        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(500), None)));

        Assert.Equal(500, await TestLedger.BalanceOfAsync(debitAccountId));
    }

    [Fact]
    public async Task DepositAsync_SameKeyAndSameRequest_ReplaysTheOriginal()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();

        var original = await TestLedger.WithTransferServiceAsync(service => service.DepositAsync(key, accountId, Usd(500), None));
        var replay = await TestLedger.WithTransferServiceAsync(service => service.DepositAsync(key, accountId, Usd(500), None));

        Assert.True(replay.WasReplayed);
        Assert.Equal(original.Transfer.Id, replay.Transfer.Id);
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
    }

    [Fact]
    public async Task TransferAsync_RejectedRequest_IsNotRememberedAndCanSucceedLater()
    {
        var debitAccountId = await TestLedger.CreateCustomerAsync();
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(50), None)));
        await TestLedger.DepositAsync(debitAccountId, Usd(50), TestLedger.BaseTime);

        var outcome = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(50), None));

        Assert.False(outcome.WasReplayed);
        Assert.Equal(50, await TestLedger.BalanceOfAsync(creditAccountId));
    }

    [Fact]
    public async Task TransferAsync_ReplayAfterTheFundsAreGone_StillReturnsTheOriginal()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        var original = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(100), None));

        var replay = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(100), None));

        Assert.True(replay.WasReplayed);
        Assert.Equal(original.Transfer.Id, replay.Transfer.Id);
        Assert.Equal(0, await TestLedger.BalanceOfAsync(debitAccountId));
    }

    [Fact]
    public async Task GetAsync_ExistingTransfer_ReturnsItWithEntries()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var created = await TestLedger.DepositAsync(accountId, Usd(100), TestLedger.BaseTime);

        var transfer = await TestLedger.WithTransferServiceAsync(service => service.GetAsync(created.Id, None));

        Assert.Equal(created.Id, transfer.Id);
        Assert.Equal(2, transfer.Entries.Count);
    }

    [Fact]
    public async Task GetAsync_UnknownTransfer_Throws()
    {
        await Assert.ThrowsAsync<TransferNotFoundException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.GetAsync(Guid.CreateVersion7(), None)));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TransferServiceTests|FullyQualifiedName~IdempotencyKeyConflictTests"`
Expected: build FAILS with `The type or namespace name 'Transfers' does not exist in the namespace 'UmutLedger.Application'`.

- [ ] **Step 4: Write the implementation**

`src/UmutLedger/Infrastructure/Persistence/IdempotencyKeyConflict.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace UmutLedger.Infrastructure.Persistence
{
    public static class IdempotencyKeyConflict
    {
        public static bool Matches(DbUpdateException exception) =>
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: LedgerConstraints.TransferIdempotencyKey
            };
    }
}
```

`src/UmutLedger/Application/Transfers/TransferOutcome.cs`:

```csharp
using UmutLedger.Domain.Entities;

namespace UmutLedger.Application.Transfers
{
    public sealed class TransferOutcome
    {
        public TransferOutcome(Transfer transfer, bool wasReplayed)
        {
            ArgumentNullException.ThrowIfNull(transfer);
            Transfer = transfer;
            WasReplayed = wasReplayed;
        }

        public Transfer Transfer { get; }

        public bool WasReplayed { get; }
    }
}
```

`src/UmutLedger/Application/Transfers/ITransferService.cs`:

```csharp
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Application.Transfers
{
    public interface ITransferService
    {
        Task<TransferOutcome> TransferAsync(IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, Money amount, CancellationToken cancellationToken);

        Task<TransferOutcome> DepositAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken);

        Task<TransferOutcome> WithdrawAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken);

        Task<Transfer> GetAsync(Guid transferId, CancellationToken cancellationToken);
    }
}
```

`src/UmutLedger/Application/Transfers/TransferService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Application.Transfers
{
    public sealed class TransferService(LedgerDbContext db, TimeProvider timeProvider) : ITransferService
    {
        public async Task<TransferOutcome> TransferAsync(
            IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, Money amount, CancellationToken cancellationToken)
        {
            if (debitAccountId == creditAccountId)
            {
                throw new SameAccountTransferException();
            }

            var instruction = new Instruction(key, debitAccountId, creditAccountId, amount, [debitAccountId, creditAccountId]);
            return await ExecuteAsync(instruction, cancellationToken);
        }

        public async Task<TransferOutcome> DepositAsync(
            IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken)
        {
            var worldAccountId = await FindWorldAccountIdAsync(amount.Currency, cancellationToken);
            return await ExecuteAsync(new Instruction(key, worldAccountId, accountId, amount, [accountId]), cancellationToken);
        }

        public async Task<TransferOutcome> WithdrawAsync(
            IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken)
        {
            var worldAccountId = await FindWorldAccountIdAsync(amount.Currency, cancellationToken);
            return await ExecuteAsync(new Instruction(key, accountId, worldAccountId, amount, [accountId]), cancellationToken);
        }

        public async Task<Transfer> GetAsync(Guid transferId, CancellationToken cancellationToken) =>
            await TransfersWithEntries().AsNoTracking().SingleOrDefaultAsync(transfer => transfer.Id == transferId, cancellationToken)
            ?? throw new TransferNotFoundException(transferId);

        private async Task<TransferOutcome> ExecuteAsync(Instruction instruction, CancellationToken cancellationToken)
        {
            instruction.Amount.EnsurePositive();
            try
            {
                return await ApplyInOneTransactionAsync(instruction, cancellationToken);
            }
            catch (DbUpdateException exception) when (IdempotencyKeyConflict.Matches(exception))
            {
                // A concurrent request with the same key committed first; this one rolled back and replays the winner.
                db.ChangeTracker.Clear();
                var winner = await TransfersWithEntries().SingleAsync(transfer => transfer.IdempotencyKey == instruction.Key, cancellationToken);
                return Replay(winner, instruction);
            }
        }

        private async Task<TransferOutcome> ApplyInOneTransactionAsync(Instruction instruction, CancellationToken cancellationToken)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var replayWithoutLocks = await FindReplayAsync(instruction, cancellationToken);
            if (replayWithoutLocks is not null)
            {
                return replayWithoutLocks;
            }

            var lockedAccounts = await LockAccountsAsync(instruction, cancellationToken);

            // A request that waited for the locks may find the winner's transfer committed in the meantime.
            var replayAfterLocks = await FindReplayAsync(instruction, cancellationToken);
            if (replayAfterLocks is not null)
            {
                return replayAfterLocks;
            }

            var debitAccount = Find(lockedAccounts, instruction.DebitAccountId);
            var creditAccount = Find(lockedAccounts, instruction.CreditAccountId);
            RejectSystemAccountsNamedByTheCaller(lockedAccounts, instruction);

            var now = timeProvider.GetUtcNow();
            var transfer = Transfer.Execute(Guid.CreateVersion7(now), instruction.Key, debitAccount, creditAccount, instruction.Amount, now);
            db.Transfers.Add(transfer);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TransferOutcome(transfer, wasReplayed: false);
        }

        // Locking in ascending id order means two opposing transfers always queue instead of deadlocking.
        private async Task<Dictionary<Guid, Account>> LockAccountsAsync(Instruction instruction, CancellationToken cancellationToken)
        {
            Guid[] accountIds = [instruction.DebitAccountId, instruction.CreditAccountId];
            var lockedAccounts = await db.Accounts
                .FromSql($"SELECT * FROM accounts WHERE id = ANY({accountIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(cancellationToken);
            return lockedAccounts.ToDictionary(account => account.Id);
        }

        private Task<Guid> FindWorldAccountIdAsync(Currency currency, CancellationToken cancellationToken) =>
            db.Accounts
                .Where(account => account.Type == AccountType.System && account.Currency == currency)
                .Select(account => account.Id)
                .SingleAsync(cancellationToken);

        private IQueryable<Transfer> TransfersWithEntries() => db.Transfers.Include(transfer => transfer.Entries);

        private static Account Find(Dictionary<Guid, Account> lockedAccounts, Guid accountId) =>
            lockedAccounts.GetValueOrDefault(accountId) ?? throw new AccountNotFoundException(accountId);

        private static void RejectSystemAccountsNamedByTheCaller(Dictionary<Guid, Account> lockedAccounts, Instruction instruction)
        {
            foreach (var accountId in instruction.AccountIdsNamedByTheCaller)
            {
                if (lockedAccounts[accountId].Type == AccountType.System)
                {
                    throw new SystemAccountNotAllowedException(accountId);
                }
            }
        }

        private static TransferOutcome Replay(Transfer existing, Instruction instruction)
        {
            if (!existing.IsSameRequest(instruction.DebitAccountId, instruction.CreditAccountId, instruction.Amount))
            {
                throw new IdempotencyKeyReusedException();
            }

            return new TransferOutcome(existing, wasReplayed: true);
        }

        private sealed record Instruction(
            IdempotencyKey Key,
            Guid DebitAccountId,
            Guid CreditAccountId,
            Money Amount,
            IReadOnlyCollection<Guid> AccountIdsNamedByTheCaller);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TransferServiceTests|FullyQualifiedName~IdempotencyKeyConflictTests"`
Expected: PASS, 28 tests.

> **Amended during execution (2026-10-11).** The Task 6 review found that the flow as first planned let a duplicate request that waited behind the winner's row locks execute again instead of replaying, so it could return `insufficient_funds` for a committed transfer. The service now looks the key up a second time once the locks are held (`FindReplayAsync`, called before and after `LockAccountsAsync`; the code above shows the corrected flow, and `FindReplayAsync` wraps the lookup and `Replay`). Two deterministic interleaving tests were added in this task as `tests/UmutLedger.Tests/Application/TransferConcurrencyTests.cs`: a loser blocked behind a winner that drains the account must replay, and the same key on unrelated accounts must be stopped by the unique constraint. Their full text is in `.superpowers/sdd/2026-10-11-core-ledger/task-6-fix1.md` and in the test file itself.

---

### Task 7: Concurrency proofs

This task adds tests only. If one fails, the defect is in `TransferService` (Task 6): fix it there.

> **Amended during execution (2026-10-11).** `TransferConcurrencyTests.cs` already exists from the Task 6 fix, with two forced-interleaving tests and the helpers `TransferAsync`, `AddUncommittedTransferAsync`, `BackendProcessIdAsync` and `WaitUntilARequestIsBlockedByAsync`. This task now **adds** the three race tests below (`OverdraftRace_OnlyTheAffordableTransfersSucceed`, `OpposingTransfers_AllCompleteWithoutDeadlock`, `DuplicateKeyRace_WritesExactlyOneTransfer`), the `ParallelRequests` constant and the `SucceededAsync` helper to that file. The fourth test in the listing below, `DuplicateKeyRace_LoserBlockedBehindTheWinner_ReplaysTheWinner`, and its two helpers are superseded by the tests already in the file: do not add them. The file ends with 5 tests.

**Files:**
- Test: `tests/UmutLedger.Tests/Application/TransferConcurrencyTests.cs`

**Interfaces:**
- Consumes: `TestLedger.WithTransferServiceAsync`, `TestLedger.CreateFundedCustomerAsync`, `TestLedger.BalanceOfAsync`, `TestDatabase.CreateContextAsync`, `LedgerInvariants.AssertHoldAsync`, `Transfer.Execute`.
- Produces: nothing used by later tasks.

- [ ] **Step 1: Write the tests**

`tests/UmutLedger.Tests/Application/TransferConcurrencyTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class TransferConcurrencyTests
{
    private const int ParallelRequests = 20;
    private const long TransferAmount = 100;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan BlockedRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BlockedRequestPollInterval = TimeSpan.FromMilliseconds(20);

    private static Task<TransferOutcome> TransferAsync(IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, long amount) =>
        Task.Run(() => TestLedger.WithTransferServiceAsync(service =>
            service.TransferAsync(key, debitAccountId, creditAccountId, TestLedger.Usd(amount), None)));

    private static async Task<bool> SucceededAsync(Task<TransferOutcome> transfer)
    {
        try
        {
            await transfer;
            return true;
        }
        catch (InsufficientFundsException)
        {
            return false;
        }
    }

    [Fact]
    public async Task OverdraftRace_OnlyTheAffordableTransfersSucceed()
    {
        const int affordableTransfers = 5;
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(affordableTransfers * TransferAmount);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var attempts = Enumerable.Range(0, ParallelRequests)
            .Select(_ => SucceededAsync(TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TransferAmount)));
        var results = await Task.WhenAll(attempts);

        Assert.Equal(affordableTransfers, results.Count(succeeded => succeeded));
        Assert.Equal(0, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(affordableTransfers * TransferAmount, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task OpposingTransfers_AllCompleteWithoutDeadlock()
    {
        const long openingBalance = ParallelRequests * TransferAmount;
        var firstAccountId = await TestLedger.CreateFundedCustomerAsync(openingBalance);
        var secondAccountId = await TestLedger.CreateFundedCustomerAsync(openingBalance);

        var transfers = Enumerable.Range(0, ParallelRequests).SelectMany(_ => new[]
        {
            TransferAsync(TestLedger.NewKey(), firstAccountId, secondAccountId, TransferAmount),
            TransferAsync(TestLedger.NewKey(), secondAccountId, firstAccountId, TransferAmount)
        });
        var outcomes = await Task.WhenAll(transfers);

        Assert.Equal(ParallelRequests * 2, outcomes.Length);
        Assert.Equal(openingBalance, await TestLedger.BalanceOfAsync(firstAccountId));
        Assert.Equal(openingBalance, await TestLedger.BalanceOfAsync(secondAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task DuplicateKeyRace_WritesExactlyOneTransfer()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(ParallelRequests * TransferAmount);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();

        var attempts = Enumerable.Range(0, ParallelRequests)
            .Select(_ => TransferAsync(key, debitAccountId, creditAccountId, TransferAmount));
        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes, outcome => !outcome.WasReplayed);
        Assert.Single(outcomes.Select(outcome => outcome.Transfer.Id).Distinct());
        Assert.Equal(TransferAmount, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    // Forces the exact interleaving the spec describes: the loser passes the key lookup, waits for the
    // winner's row locks, then hits the unique constraint when the winner commits.
    [Fact]
    public async Task DuplicateKeyRace_LoserBlockedBehindTheWinner_ReplaysTheWinner()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(2 * TransferAmount);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();

        await using var winnerDb = await TestDatabase.CreateContextAsync();
        await using var winnerTransaction = await winnerDb.Database.BeginTransactionAsync();
        var winnerProcessId = await winnerDb.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var winner = Transfer.Execute(
            Guid.CreateVersion7(),
            key,
            await winnerDb.Accounts.SingleAsync(account => account.Id == debitAccountId),
            await winnerDb.Accounts.SingleAsync(account => account.Id == creditAccountId),
            TestLedger.Usd(TransferAmount),
            TestLedger.BaseTime);
        winnerDb.Transfers.Add(winner);
        await winnerDb.SaveChangesAsync();

        var loser = TransferAsync(key, debitAccountId, creditAccountId, TransferAmount);
        await WaitUntilARequestIsBlockedByAsync(winnerProcessId);
        await winnerTransaction.CommitAsync();
        var outcome = await loser;

        Assert.True(outcome.WasReplayed);
        Assert.Equal(winner.Id, outcome.Transfer.Id);
        Assert.Equal(TransferAmount, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(TransferAmount, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    private static async Task WaitUntilARequestIsBlockedByAsync(int blockingProcessId)
    {
        using var timeout = new CancellationTokenSource(BlockedRequestTimeout);
        await using var probe = await TestDatabase.CreateContextAsync();
        while (await CountRequestsBlockedByAsync(probe, blockingProcessId) == 0)
        {
            await Task.Delay(BlockedRequestPollInterval, timeout.Token);
        }
    }

    private static Task<int> CountRequestsBlockedByAsync(DbContext probe, int blockingProcessId) =>
        probe.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {blockingProcessId} = ANY(pg_blocking_pids(pid))")
            .SingleAsync();
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~TransferConcurrencyTests"`
Expected: PASS, 4 tests.

What a failure means:
- `40P01 deadlock detected` in `OpposingTransfers`: the lock query is not ordering by id.
- More than 5 successes or a negative balance in `OverdraftRace`: the accounts are not being locked before the funds check.
- `23505` escaping from a duplicate-key test: the conflict handler in `ExecuteAsync` is not catching the unique violation.

- [ ] **Step 3: Run the tests five more times to check they are stable**

Run: `1..5 | ForEach-Object { dotnet test --no-build --filter "FullyQualifiedName~TransferConcurrencyTests" }`
Expected: PASS all five times.

---

### Task 8: Error handling and the accounts API

**Files:**
- Modify: `src/UmutLedger/Program.cs` (final composition root)
- Create: `src/UmutLedger/Infrastructure/Persistence/DatabaseFailure.cs`
- Create: `src/UmutLedger/Api/Errors/LedgerProblem.cs`, `LedgerExceptionHandler.cs`, `InvalidModelStateResponse.cs`
- Create: `src/UmutLedger/Api/Models/CreateAccountRequest.cs`, `AccountResponse.cs`, `EntryResponse.cs`, `EntryPageResponse.cs`
- Create: `src/UmutLedger/Api/Controllers/AccountsController.cs`
- Create: `tests/UmutLedger.Tests/Support/LedgerApi.cs`, `ApiExtensions.cs`, `ThrowingController.cs`
- Test: `tests/UmutLedger.Tests/Infrastructure/DatabaseFailureTests.cs`, `tests/UmutLedger.Tests/Api/AccountsApiTests.cs`, `tests/UmutLedger.Tests/Api/ErrorHandlingApiTests.cs`

**Interfaces:**
- Consumes: `IAccountService`, `AccountService`, `PageRequest`, `EntryPage` (Task 5); `ITransferService`, `TransferService` (Task 6); `LedgerException`, `ErrorKind`, `ErrorCodes` (Task 1); `LedgerDbContext` (Task 4).
- Produces:
  - `static class DatabaseFailure` with `static bool IsUnavailable(Exception exception)`.
  - `static class LedgerProblem` with `const string ContentType = "application/problem+json"`, `static ProblemDetails Create(HttpContext httpContext, int status, string code, string detail)` and `static Task WriteAsync(HttpContext httpContext, int status, string code, string detail, CancellationToken cancellationToken)`. Every problem body has `status`, `title`, `detail`, `instance`, `code` and `traceId`.
  - Response records: `AccountResponse(Guid Id, string Name, Currency Currency, AccountType Type, long Balance, DateTimeOffset CreatedAt)`, `EntryResponse(Guid Id, Guid TransferId, Guid AccountId, long Amount, DateTimeOffset CreatedAt)`, `EntryPageResponse(IReadOnlyList<EntryResponse> Items, Guid? NextCursor)`, each with a static `From` mapper.
  - JSON: camelCase property names, enums as strings, integer enum values refused.
  - Test support: `LedgerApi` (class fixture with `HttpClient Client` and `static WebApplicationFactory<Program> CreateFactory(string connectionString, string environment)`), `ApiExtensions.PostLedgerAsync(this HttpClient, string url, object body, string? idempotencyKey = null)`, `ApiExtensions.ReadAsync<T>(this HttpResponseMessage)`, `ApiExtensions.AssertProblemAsync(this HttpResponseMessage, HttpStatusCode status, string code)`.

- [ ] **Step 1: Write the test support code**

`tests/UmutLedger.Tests/Support/ThrowingController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;

namespace UmutLedger.Tests.Support;

[ApiController]
[Route("test/throw")]
public sealed class ThrowingController : ControllerBase
{
    public const string SecretMessage = "secret-internal-detail";

    [HttpGet]
    public IActionResult Throw() => throw new InvalidOperationException(SecretMessage);
}
```

`tests/UmutLedger.Tests/Support/LedgerApi.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace UmutLedger.Tests.Support;

public sealed class LedgerApi : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    public static WebApplicationFactory<Program> CreateFactory(string connectionString, string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString }));
            builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(ThrowingController).Assembly));
        });

    public async Task InitializeAsync()
    {
        _factory = CreateFactory(await TestDatabase.GetConnectionStringAsync(), Environments.Development);
        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();
}
```

`tests/UmutLedger.Tests/Support/ApiExtensions.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmutLedger.Tests.Support;

public static class ApiExtensions
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string ProblemContentType = "application/problem+json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static Task<HttpResponseMessage> PostLedgerAsync(this HttpClient client, string url, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyKeyHeader, idempotencyKey);
        }

        return client.SendAsync(request);
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;

    public static async Task AssertProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(ProblemContentType, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.ReadAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/UmutLedger.Tests/Infrastructure/DatabaseFailureTests.cs`:

```csharp
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Tests.Infrastructure;

public class DatabaseFailureTests
{
    [Fact]
    public void IsUnavailable_ConnectionFailure_IsTrue()
    {
        Assert.True(DatabaseFailure.IsUnavailable(new NpgsqlException("Failed to connect.", new SocketException())));
    }

    [Fact]
    public void IsUnavailable_Timeout_IsTrue()
    {
        Assert.True(DatabaseFailure.IsUnavailable(new TimeoutException()));
    }

    [Fact]
    public void IsUnavailable_ConnectionFailureWrappedByAnotherException_IsTrue()
    {
        var connectionFailure = new NpgsqlException("Failed to connect.", new IOException());

        Assert.True(DatabaseFailure.IsUnavailable(new InvalidOperationException("Query failed.", connectionFailure)));
    }

    [Fact]
    public void IsUnavailable_ConstraintViolation_IsFalse()
    {
        var violation = new PostgresException("check violated", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation);

        Assert.False(DatabaseFailure.IsUnavailable(new DbUpdateException("Save failed.", violation)));
    }

    [Fact]
    public void IsUnavailable_UnrelatedException_IsFalse()
    {
        Assert.False(DatabaseFailure.IsUnavailable(new InvalidOperationException()));
    }
}
```

`tests/UmutLedger.Tests/Api/AccountsApiTests.cs`:

```csharp
using System.Net;
using UmutLedger.Api.Models;
using UmutLedger.Domain.Enums;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class AccountsApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private readonly HttpClient _client = api.Client;

    [Fact]
    public async Task CreateAccount_ValidRequest_Returns201WithTheAccountAndItsLocation()
    {
        var response = await _client.PostLedgerAsync("/accounts", new { name = "Alice", currency = "EUR" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = await response.ReadAsync<AccountResponse>();
        Assert.Equal("Alice", account.Name);
        Assert.Equal(Currency.EUR, account.Currency);
        Assert.Equal(AccountType.Customer, account.Type);
        Assert.Equal(0, account.Balance);
        Assert.EndsWith($"/accounts/{account.Id}", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("""{"name":"   ","currency":"USD"}""")]
    [InlineData("""{"name":"Alice","currency":"JPY"}""")]
    [InlineData("""{"name":"Alice","currency":1}""")]
    [InlineData("""{"name":"Alice"}""")]
    [InlineData("""{"currency":"USD"}""")]
    [InlineData("""{"name":null,"currency":"USD"}""")]
    [InlineData("""{"name":"Alice","currency":""")]
    [InlineData("")]
    public async Task CreateAccount_InvalidBody_Returns400ValidationFailed(string json)
    {
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/accounts", content);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task CreateAccount_NameOverTheLimit_Returns400ValidationFailed()
    {
        var response = await _client.PostLedgerAsync("/accounts", new { name = new string('a', 101), currency = "USD" });

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task CreateAccount_WrongContentType_Returns415()
    {
        var content = new StringContent("name=Alice", System.Text.Encoding.UTF8, "text/plain");

        var response = await _client.PostAsync("/accounts", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task GetAccount_ExistingAccount_Returns200WithItsBalance()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(750);

        var response = await _client.GetAsync($"/accounts/{accountId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var account = await response.ReadAsync<AccountResponse>();
        Assert.Equal(accountId, account.Id);
        Assert.Equal(750, account.Balance);
    }

    [Fact]
    public async Task GetAccount_SystemAccount_IsReadable()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.GBP);

        var response = await _client.GetAsync($"/accounts/{worldAccountId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AccountType.System, (await response.ReadAsync<AccountResponse>()).Type);
    }

    [Fact]
    public async Task GetAccount_UnknownAccount_Returns404AccountNotFound()
    {
        var response = await _client.GetAsync($"/accounts/{Guid.CreateVersion7()}");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
    }

    [Fact]
    public async Task GetAccount_IdIsNotAGuid_Returns400ValidationFailed()
    {
        var response = await _client.GetAsync("/accounts/not-a-guid");

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task GetEntries_SeveralEntries_PagesNewestFirstWithACursor()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime.AddSeconds(1));
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(200), TestLedger.BaseTime.AddSeconds(2));
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(300), TestLedger.BaseTime.AddSeconds(3));

        var firstPage = await (await _client.GetAsync($"/accounts/{accountId}/entries?limit=2")).ReadAsync<EntryPageResponse>();
        var secondPage = await (await _client.GetAsync($"/accounts/{accountId}/entries?limit=2&cursor={firstPage.NextCursor}")).ReadAsync<EntryPageResponse>();

        Assert.Equal([300, 200], firstPage.Items.Select(entry => entry.Amount));
        Assert.NotNull(firstPage.NextCursor);
        Assert.Equal([100], secondPage.Items.Select(entry => entry.Amount));
        Assert.Null(secondPage.NextCursor);
        Assert.All(firstPage.Items, entry => Assert.Equal(accountId, entry.AccountId));
    }

    [Fact]
    public async Task GetEntries_NoLimit_Returns200()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        var response = await _client.GetAsync($"/accounts/{accountId}/entries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await response.ReadAsync<EntryPageResponse>()).Items);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("limit=abc")]
    [InlineData("cursor=not-a-guid")]
    public async Task GetEntries_InvalidPaging_Returns400ValidationFailed(string query)
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.GetAsync($"/accounts/{accountId}/entries?{query}");

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task GetEntries_UnknownAccount_Returns404AccountNotFound()
    {
        var response = await _client.GetAsync($"/accounts/{Guid.CreateVersion7()}/entries");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
    }
}
```

`tests/UmutLedger.Tests/Api/ErrorHandlingApiTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Hosting;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class ErrorHandlingApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=ledger;Username=ledger;Password=ledger;Timeout=2";

    [Fact]
    public async Task UnexpectedException_Returns500WithoutLeakingDetails()
    {
        var response = await api.Client.GetAsync("/test/throw");

        var body = await response.Content.ReadAsStringAsync();
        await response.AssertProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain(ThrowingController.SecretMessage, body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task DatabaseUnreachable_Returns503ServiceUnavailable()
    {
        await using var factory = LedgerApi.CreateFactory(UnreachableDatabase, Environments.Production);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/accounts/{Guid.CreateVersion7()}");

        await response.AssertProblemAsync(HttpStatusCode.ServiceUnavailable, "service_unavailable");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Api|FullyQualifiedName~DatabaseFailureTests"`
Expected: build FAILS with `The type or namespace name 'Api' does not exist in the namespace 'UmutLedger'`.

- [ ] **Step 4: Write the error handling**

`src/UmutLedger/Infrastructure/Persistence/DatabaseFailure.cs`:

```csharp
using Npgsql;

namespace UmutLedger.Infrastructure.Persistence
{
    public static class DatabaseFailure
    {
        public static bool IsUnavailable(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is TimeoutException or NpgsqlException { IsTransient: true })
                {
                    return true;
                }
            }

            return false;
        }
    }
}
```

`src/UmutLedger/Api/Errors/LedgerProblem.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace UmutLedger.Api.Errors
{
    public static class LedgerProblem
    {
        public const string ContentType = "application/problem+json";
        private const string CodeExtension = "code";
        private const string TraceIdExtension = "traceId";

        public static ProblemDetails Create(HttpContext httpContext, int status, string code, string detail)
        {
            var problem = new ProblemDetails
            {
                Status = status,
                Title = ReasonPhrases.GetReasonPhrase(status),
                Detail = detail,
                Instance = httpContext.Request.Path
            };
            problem.Extensions[CodeExtension] = code;
            problem.Extensions[TraceIdExtension] = httpContext.TraceIdentifier;
            return problem;
        }

        public static Task WriteAsync(HttpContext httpContext, int status, string code, string detail, CancellationToken cancellationToken)
        {
            httpContext.Response.StatusCode = status;
            return httpContext.Response.WriteAsJsonAsync(
                Create(httpContext, status, code, detail), options: null, contentType: ContentType, cancellationToken);
        }
    }
}
```

`src/UmutLedger/Api/Errors/LedgerExceptionHandler.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Api.Errors
{
    public sealed class LedgerExceptionHandler(ILogger<LedgerExceptionHandler> logger) : IExceptionHandler
    {
        private const string UnavailableDetail = "The service is temporarily unavailable. Retry the request with the same idempotency key.";
        private const string UnexpectedDetail = "An unexpected error occurred.";

        private static readonly IReadOnlyDictionary<ErrorKind, int> StatusByKind = new Dictionary<ErrorKind, int>
        {
            [ErrorKind.InvalidRequest] = StatusCodes.Status400BadRequest,
            [ErrorKind.NotFound] = StatusCodes.Status404NotFound,
            [ErrorKind.RuleViolation] = StatusCodes.Status422UnprocessableEntity
        };

        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            var (status, code, detail) = Describe(exception);
            await LedgerProblem.WriteAsync(httpContext, status, code, detail, cancellationToken);
            return true;
        }

        private (int Status, string Code, string Detail) Describe(Exception exception)
        {
            if (exception is LedgerException ledgerException)
            {
                return (StatusByKind[ledgerException.Kind], ledgerException.Code, ledgerException.Message);
            }

            if (DatabaseFailure.IsUnavailable(exception))
            {
                logger.LogError(exception, "The database is unavailable.");
                return (StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable, UnavailableDetail);
            }

            logger.LogError(exception, "An unexpected error occurred.");
            return (StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, UnexpectedDetail);
        }
    }
}
```

`src/UmutLedger/Api/Errors/InvalidModelStateResponse.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Api.Errors
{
    public static class InvalidModelStateResponse
    {
        private const string MessageSeparator = " ";

        public static IActionResult Create(ActionContext context)
        {
            var messages = context.ModelState.Values
                .SelectMany(entry => entry.Errors)
                .Select(error => error.ErrorMessage);
            var problem = LedgerProblem.Create(
                context.HttpContext,
                StatusCodes.Status400BadRequest,
                ErrorCodes.ValidationFailed,
                $"The request is not valid. {string.Join(MessageSeparator, messages)}");

            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { LedgerProblem.ContentType }
            };
        }
    }
}
```

- [ ] **Step 5: Write the models and the controller**

`src/UmutLedger/Api/Models/CreateAccountRequest.cs`:

```csharp
using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models
{
    public sealed class CreateAccountRequest
    {
        public required string Name { get; init; }

        public required Currency Currency { get; init; }
    }
}
```

`src/UmutLedger/Api/Models/AccountResponse.cs`:

```csharp
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models
{
    public sealed record AccountResponse(Guid Id, string Name, Currency Currency, AccountType Type, long Balance, DateTimeOffset CreatedAt)
    {
        public static AccountResponse From(Account account) =>
            new(account.Id, account.Name, account.Currency, account.Type, account.Balance, account.CreatedAt);
    }
}
```

`src/UmutLedger/Api/Models/EntryResponse.cs`:

```csharp
using UmutLedger.Domain.Entities;

namespace UmutLedger.Api.Models
{
    public sealed record EntryResponse(Guid Id, Guid TransferId, Guid AccountId, long Amount, DateTimeOffset CreatedAt)
    {
        public static EntryResponse From(Entry entry) =>
            new(entry.Id, entry.TransferId, entry.AccountId, entry.Amount, entry.CreatedAt);
    }
}
```

`src/UmutLedger/Api/Models/EntryPageResponse.cs`:

```csharp
using UmutLedger.Application.Accounts;

namespace UmutLedger.Api.Models
{
    public sealed record EntryPageResponse(IReadOnlyList<EntryResponse> Items, Guid? NextCursor)
    {
        public static EntryPageResponse From(EntryPage page) =>
            new(page.Items.Select(EntryResponse.From).ToList(), page.NextCursor);
    }
}
```

`src/UmutLedger/Api/Controllers/AccountsController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using UmutLedger.Api.Models;
using UmutLedger.Application.Accounts;

namespace UmutLedger.Api.Controllers
{
    [ApiController]
    [Route("accounts")]
    public sealed class AccountsController(IAccountService accounts) : ControllerBase
    {
        [HttpPost]
        public async Task<ActionResult<AccountResponse>> Create(CreateAccountRequest request, CancellationToken cancellationToken)
        {
            var account = await accounts.CreateAsync(request.Name, request.Currency, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = account.Id }, AccountResponse.From(account));
        }

        [HttpGet("{id}")]
        public async Task<AccountResponse> Get(Guid id, CancellationToken cancellationToken) =>
            AccountResponse.From(await accounts.GetAsync(id, cancellationToken));

        [HttpGet("{id}/entries")]
        public async Task<EntryPageResponse> GetEntries(Guid id, [FromQuery] int? limit, [FromQuery] Guid? cursor, CancellationToken cancellationToken) =>
            EntryPageResponse.From(await accounts.GetEntriesAsync(id, new PageRequest(limit, cursor), cancellationToken));
    }
}
```

- [ ] **Step 6: Replace `Program.cs` with the final composition root**

Replace the whole of `src/UmutLedger/Program.cs`. The template's `UseAuthorization` call is removed because the API has no authentication.

```csharp
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using UmutLedger.Api.Errors;
using UmutLedger.Application.Accounts;
using UmutLedger.Application.Transfers;
using UmutLedger.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = InvalidModelStateResponse.Create)
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<LedgerDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString(LedgerConstraints.ConnectionStringName)));
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<ITransferService, TransferService>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
```

The .NET 10 Web SDK generates a public `Program` class, which is what `WebApplicationFactory<Program>` needs. If the test project reports that `Program` is inaccessible, add `public partial class Program;` as the last line of this file.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~UmutLedger.Tests.Api|FullyQualifiedName~DatabaseFailureTests"`
Expected: PASS, 29 tests. The 503 test takes about two seconds because of the connection timeout.

---

### Task 9: Transfers API

**Files:**
- Create: `src/UmutLedger/Api/Models/MoneyRequest.cs`, `CreateTransferRequest.cs`, `TransferResponse.cs`
- Create: `src/UmutLedger/Api/Controllers/TransfersController.cs`
- Test: `tests/UmutLedger.Tests/Api/TransfersApiTests.cs`

**Interfaces:**
- Consumes: `ITransferService`, `TransferOutcome` (Task 6); `IdempotencyKey`, `Money` (Tasks 1 and 3); `EntryResponse`, `LedgerApi`, `ApiExtensions` (Task 8).
- Produces:
  - `MoneyRequest { required long Amount; required Currency Currency }` (body of deposits and withdrawals).
  - `CreateTransferRequest { required Guid DebitAccountId; required Guid CreditAccountId; required long Amount; required Currency Currency }`.
  - `TransferResponse(Guid Id, string IdempotencyKey, Guid DebitAccountId, Guid CreditAccountId, long Amount, Currency Currency, DateTimeOffset CreatedAt, IReadOnlyList<EntryResponse> Entries)` with `static TransferResponse From(Transfer transfer)`.
  - Endpoints `POST /transfers`, `POST /accounts/{id}/deposits`, `POST /accounts/{id}/withdrawals`, `GET /transfers/{id}`. A new transfer returns 201 with a `Location` header; a replay returns 200.

- [ ] **Step 1: Write the failing tests**

`tests/UmutLedger.Tests/Api/TransfersApiTests.cs`:

```csharp
using System.Net;
using UmutLedger.Api.Models;
using UmutLedger.Domain.Enums;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class TransfersApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private readonly HttpClient _client = api.Client;

    private static string NewKey() => Guid.NewGuid().ToString();

    private static object Usd(long amount) => new { amount, currency = "USD" };

    private static object TransferBody(Guid debitAccountId, Guid creditAccountId, long amount, string currency = "USD") =>
        new { debitAccountId, creditAccountId, amount, currency };

    [Fact]
    public async Task Deposit_ValidRequest_Returns201WithTheTransferAndItsLocation()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transfer = await response.ReadAsync<TransferResponse>();
        Assert.Equal(key, transfer.IdempotencyKey);
        Assert.Equal(accountId, transfer.CreditAccountId);
        Assert.Equal(500, transfer.Amount);
        Assert.Equal(Currency.USD, transfer.Currency);
        Assert.Equal(2, transfer.Entries.Count);
        Assert.Equal(0, transfer.Entries.Sum(entry => entry.Amount));
        Assert.EndsWith($"/transfers/{transfer.Id}", response.Headers.Location!.ToString());
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Deposit_SameKeyAndSameRequest_Returns200WithTheOriginalTransfer()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        var original = await (await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), key)).ReadAsync<TransferResponse>();

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original.Id, (await response.ReadAsync<TransferResponse>()).Id);
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Deposit_WithoutIdempotencyKey_Returns400KeyRequired()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500));

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        Assert.Equal(0, await TestLedger.BalanceOfAsync(accountId));
    }

    [Fact]
    public async Task Deposit_KeyOverTheLimit_Returns400ValidationFailed()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), new string('k', 256));

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Theory]
    [InlineData("""{"amount":0,"currency":"USD"}""")]
    [InlineData("""{"amount":-5,"currency":"USD"}""")]
    [InlineData("""{"amount":10.5,"currency":"USD"}""")]
    [InlineData("""{"amount":100,"currency":"JPY"}""")]
    [InlineData("""{"currency":"USD"}""")]
    [InlineData("""{"amount":100}""")]
    public async Task Deposit_InvalidBody_Returns400ValidationFailed(string json)
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{accountId}/deposits")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(ApiExtensions.IdempotencyKeyHeader, NewKey());

        var response = await _client.SendAsync(request);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(0, await TestLedger.BalanceOfAsync(accountId));
    }

    [Fact]
    public async Task Deposit_UnknownAccount_Returns404AccountNotFound()
    {
        var response = await _client.PostLedgerAsync($"/accounts/{Guid.CreateVersion7()}/deposits", Usd(500), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
    }

    [Fact]
    public async Task Deposit_CurrencyDiffersFromTheAccount_Returns422CurrencyMismatch()
    {
        var accountId = await TestLedger.CreateCustomerAsync(Currency.EUR);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "currency_mismatch");
    }

    [Fact]
    public async Task Deposit_IntoASystemAccount_Returns422SystemAccountNotAllowed()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        var response = await _client.PostLedgerAsync($"/accounts/{worldAccountId}/deposits", Usd(500), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "system_account_not_allowed");
    }

    [Fact]
    public async Task Withdrawal_WithEnoughFunds_Returns201AndReducesTheBalance()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(500);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(200), NewKey());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(accountId, (await response.ReadAsync<TransferResponse>()).DebitAccountId);
        Assert.Equal(300, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Withdrawal_MoreThanTheBalance_Returns422InsufficientFunds()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(101), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "insufficient_funds");
        Assert.Equal(100, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_ValidRequest_Returns201AndMovesTheMoney()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 400), NewKey());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transfer = await response.ReadAsync<TransferResponse>();
        Assert.Equal(debitAccountId, transfer.DebitAccountId);
        Assert.Equal(creditAccountId, transfer.CreditAccountId);
        Assert.EndsWith($"/transfers/{transfer.Id}", response.Headers.Location!.ToString());
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(400, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_SameKeyAndSameRequest_Returns200WithoutMovingMoneyAgain()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        var body = TransferBody(debitAccountId, creditAccountId, 400);
        var original = await (await _client.PostLedgerAsync("/transfers", body, key)).ReadAsync<TransferResponse>();

        var response = await _client.PostLedgerAsync("/transfers", body, key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original.Id, (await response.ReadAsync<TransferResponse>()).Id);
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
    }

    [Fact]
    public async Task Transfer_SameKeyButDifferentAmount_Returns422KeyReused()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 400), key);

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 401), key);

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
    }

    [Fact]
    public async Task Transfer_KeyAlreadyUsedByADeposit_Returns422KeyReused()
    {
        var debitAccountId = await TestLedger.CreateCustomerAsync();
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        await _client.PostLedgerAsync($"/accounts/{debitAccountId}/deposits", Usd(500), key);

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 500), key);

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(500, await TestLedger.BalanceOfAsync(debitAccountId));
    }

    [Fact]
    public async Task Transfer_SameAccountOnBothSides_Returns400SameAccountTransfer()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(accountId, accountId, 10), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "same_account_transfer");
    }

    [Fact]
    public async Task Transfer_WithoutIdempotencyKey_Returns400KeyRequired()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 10));

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
    }

    [Fact]
    public async Task Transfer_UnknownAccount_Returns404AccountNotFound()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, Guid.CreateVersion7(), 10), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
        Assert.Equal(100, await TestLedger.BalanceOfAsync(debitAccountId));
    }

    [Fact]
    public async Task Transfer_CurrencyDiffersFromTheAccounts_Returns422CurrencyMismatch()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 10, "EUR"), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "currency_mismatch");
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_Returns422InsufficientFunds()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 101), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "insufficient_funds");
    }

    [Fact]
    public async Task Transfer_NamesASystemAccount_Returns422SystemAccountNotAllowed()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(worldAccountId, creditAccountId, 10), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "system_account_not_allowed");
        Assert.Equal(0, await TestLedger.BalanceOfAsync(creditAccountId));
    }

    [Fact]
    public async Task GetTransfer_ExistingTransfer_Returns200WithItsEntries()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var created = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);

        var response = await _client.GetAsync($"/transfers/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transfer = await response.ReadAsync<TransferResponse>();
        Assert.Equal(created.Id, transfer.Id);
        Assert.Equal(2, transfer.Entries.Count);
    }

    [Fact]
    public async Task GetTransfer_UnknownTransfer_Returns404TransferNotFound()
    {
        var response = await _client.GetAsync($"/transfers/{Guid.CreateVersion7()}");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "transfer_not_found");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TransfersApiTests"`
Expected: build FAILS with `The type or namespace name 'TransferResponse' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/UmutLedger/Api/Models/MoneyRequest.cs`:

```csharp
using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models
{
    public sealed class MoneyRequest
    {
        public required long Amount { get; init; }

        public required Currency Currency { get; init; }
    }
}
```

`src/UmutLedger/Api/Models/CreateTransferRequest.cs`:

```csharp
using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models
{
    public sealed class CreateTransferRequest
    {
        public required Guid DebitAccountId { get; init; }

        public required Guid CreditAccountId { get; init; }

        public required long Amount { get; init; }

        public required Currency Currency { get; init; }
    }
}
```

`src/UmutLedger/Api/Models/TransferResponse.cs`:

```csharp
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models
{
    public sealed record TransferResponse(
        Guid Id,
        string IdempotencyKey,
        Guid DebitAccountId,
        Guid CreditAccountId,
        long Amount,
        Currency Currency,
        DateTimeOffset CreatedAt,
        IReadOnlyList<EntryResponse> Entries)
    {
        public static TransferResponse From(Transfer transfer) =>
            new(
                transfer.Id,
                transfer.IdempotencyKey.Value,
                transfer.DebitAccountId,
                transfer.CreditAccountId,
                transfer.Amount,
                transfer.Currency,
                transfer.CreatedAt,
                transfer.Entries.Select(EntryResponse.From).ToList());
    }
}
```

`src/UmutLedger/Api/Controllers/TransfersController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using UmutLedger.Api.Models;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Api.Controllers
{
    [ApiController]
    public sealed class TransfersController(ITransferService transfers) : ControllerBase
    {
        private const string IdempotencyKeyHeader = "Idempotency-Key";

        [HttpPost("transfers")]
        public async Task<ActionResult<TransferResponse>> Create(
            [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
            CreateTransferRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await transfers.TransferAsync(
                new IdempotencyKey(idempotencyKey),
                request.DebitAccountId,
                request.CreditAccountId,
                new Money(request.Amount, request.Currency),
                cancellationToken));

        [HttpPost("accounts/{id}/deposits")]
        public async Task<ActionResult<TransferResponse>> Deposit(
            Guid id,
            [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
            MoneyRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await transfers.DepositAsync(
                new IdempotencyKey(idempotencyKey), id, new Money(request.Amount, request.Currency), cancellationToken));

        [HttpPost("accounts/{id}/withdrawals")]
        public async Task<ActionResult<TransferResponse>> Withdraw(
            Guid id,
            [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
            MoneyRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await transfers.WithdrawAsync(
                new IdempotencyKey(idempotencyKey), id, new Money(request.Amount, request.Currency), cancellationToken));

        [HttpGet("transfers/{id}")]
        public async Task<TransferResponse> Get(Guid id, CancellationToken cancellationToken) =>
            TransferResponse.From(await transfers.GetAsync(id, cancellationToken));

        private ActionResult<TransferResponse> ToResult(TransferOutcome outcome)
        {
            var response = TransferResponse.From(outcome.Transfer);
            return outcome.WasReplayed
                ? Ok(response)
                : CreatedAtAction(nameof(Get), new { id = response.Id }, response);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TransfersApiTests"`
Expected: PASS, 27 tests.

---

### Task 10: Coverage gate and README

**Files:**
- Create: `README.md`
- Modify: `src/UmutLedger/UmutLedger.http` (replace the template sample)
- Modify: any file the coverage report shows as not fully covered

**Interfaces:**
- Consumes: everything from Tasks 1 to 9.
- Produces: a green full test run at 100% line and branch coverage, and the project documentation.

- [ ] **Step 1: Run the full suite with the coverage gate**

Run: `dotnet test -p:CollectCoverage=true -p:CoverletOutputFormat=cobertura`
Expected: every test passes and the Coverlet table shows `UmutLedger | 100% | 100% | 100%`. The command exits with an error if line or branch coverage is below 100%.

- [ ] **Step 2: Close any gap the gate reports**

If coverage is below 100%, open `tests/UmutLedger.Tests/coverage.cobertura.xml` and search for `hits="0"` and for `condition-coverage` values other than `100%`. For each one:

- If the line is real product code, add the missing test to the test class of the task that owns that file, following that class's existing style, then re-run Step 1.
- If the line is in a compiler-generated or source-generated file (a path under `obj/`), add that path pattern to `<ExcludeByFile>` in `tests/UmutLedger.Tests/UmutLedger.Tests.csproj`, comma-separated, and re-run Step 1.
- If the line can never run, it is dead code: delete it and re-run Step 1.

Never lower `<Threshold>` and never add `[ExcludeFromCodeCoverage]` to product code.

- [ ] **Step 3: Replace the sample requests**

Replace the whole of `src/UmutLedger/UmutLedger.http`:

```http
@UmutLedger_HostAddress = http://localhost:5289

### Create an account
POST {{UmutLedger_HostAddress}}/accounts
Content-Type: application/json

{ "name": "Alice", "currency": "USD" }

### Deposit 100.00 USD (replace the account id)
POST {{UmutLedger_HostAddress}}/accounts/00000000-0000-0000-0000-000000000000/deposits
Content-Type: application/json
Idempotency-Key: deposit-0001

{ "amount": 10000, "currency": "USD" }

### Transfer 25.00 USD between two accounts (replace the account ids)
POST {{UmutLedger_HostAddress}}/transfers
Content-Type: application/json
Idempotency-Key: transfer-0001

{
  "debitAccountId": "00000000-0000-0000-0000-000000000000",
  "creditAccountId": "00000000-0000-0000-0000-000000000000",
  "amount": 2500,
  "currency": "USD"
}
```

- [ ] **Step 4: Write the README**

Create `README.md`:

````markdown
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

The API listens on `http://localhost:5289`. Sample requests are in `src/UmutLedger/UmutLedger.http`. Amounts are in minor units, so `10000` is 100.00.

## Run the tests

Docker Desktop must be running. The tests start their own PostgreSQL container.

```powershell
dotnet test -p:CollectCoverage=true
```

The run fails if line or branch coverage is below 100%.

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

Errors are RFC 9457 problem details with a stable `code`, for example `insufficient_funds` or `idempotency_key_reused`.

## Design notes

**Double-entry with world accounts.** Money cannot appear or vanish. Each currency has one system "world" account that stands for everything outside the ledger. A deposit debits the world account and credits the customer; a withdrawal does the reverse. The world account is the only account allowed to go negative.

**Correctness under concurrency.** A transfer runs in one database transaction. It locks both account rows with `SELECT ... FOR UPDATE` in ascending id order, so opposing transfers queue instead of deadlocking. A unique constraint on the idempotency key lets only one of two identical concurrent requests commit; the other returns the winner's transfer.

**The database is the last line of defence.** `CHECK` constraints refuse a negative customer balance or a zero amount, and a trigger makes transfers and entries append-only. A mistake is corrected by a new reversing transfer, never by an edit.

**No full Clean Architecture, on purpose.** The service has a handful of operations. It keeps what pays for itself: a pure, self-validating domain, thin controllers, and services behind interfaces. It leaves out repository interfaces and a project per layer, because the tests use real PostgreSQL (so there is nothing to swap for a fake) and the row locks and constraints are part of the design rather than a detail to hide.

**Known trade-off.** Deposits and withdrawals in one currency all lock that currency's world account, so they run one at a time. Transfers between customers are not affected.

## Not in this version

Authentication, observability (OpenTelemetry, Prometheus, Grafana), the Docker image and Kubernetes deployment, and load tests are planned as separate pieces of work.
````

- [ ] **Step 5: Verify the application starts against local Postgres**

```powershell
docker compose up -d
dotnet tool restore
dotnet ef database update --project src/UmutLedger
dotnet run --project src/UmutLedger
```

Expected: `Now listening on: http://localhost:5289`. In a second terminal:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5289/accounts -ContentType "application/json" -Body '{"name":"Alice","currency":"USD"}'
```

Expected: a JSON account with `"balance": 0` and `"type": "Customer"`. Stop the app with Ctrl+C, then run `docker compose down`.

- [ ] **Step 6: Final full run**

Run: `dotnet build -warnaserror` then `dotnet test -p:CollectCoverage=true`
Expected: the build has 0 warnings, all tests pass, and coverage is 100% line and branch.
