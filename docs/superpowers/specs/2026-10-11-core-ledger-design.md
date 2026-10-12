# Core Ledger Design

Date: 2026-10-11
Status: awaiting review

## 1. Purpose and scope

UmutLedger is a banking ledger Web API that moves artificial funds between accounts. It is a portfolio piece, so the design favours choices that a reviewer from a payments background would recognise and can be defended one by one.

This spec covers the first sub-project, the core ledger:

- Accounts, deposits, withdrawals and transfers
- Idempotent money movement
- PostgreSQL persistence
- xUnit and Testcontainers tests at 100% coverage

Out of scope, each a later sub-project with its own spec:

- Observability (OpenTelemetry, Prometheus, Grafana)
- The API's Docker image and Kubernetes deployment
- Load testing (NBomber or k6)
- Authentication

Logging in this sub-project is limited to .NET's built-in `ILogger` writing to the console, which is enough to record errors with their trace id. Traces, metrics and dashboards arrive with the observability sub-project, which is next after this one.

The API has no authentication. It acts as an internal ledger service that sits behind a gateway and trusts its callers. This is a deliberate boundary, not an omission.

## 2. Decisions

| Topic | Decision |
|---|---|
| Ledger model | Double-entry. Every transfer writes two immutable entries that sum to zero |
| Money entering and leaving | One system "world" account per currency. A deposit or withdrawal is an ordinary transfer against it |
| Currencies | USD, EUR, GBP. Same-currency transfers only |
| Concurrency | Pessimistic row locks (`SELECT ... FOR UPDATE`) taken in ascending account id order |
| Idempotency | Unique idempotency key written in the same transaction as the transfer |
| Architecture | One API project with layer folders. No full Clean Architecture, no repository interfaces |
| Money type | `long` in minor units. No `decimal` or floating point in the money path |

### What a world account is

In a double-entry ledger money cannot appear or vanish: every credit to one account needs a matching debit on another. A world account is the other side for money that crosses the ledger's edge. It stands for "everything outside this ledger" in one currency, and there is exactly one per currency.

- A deposit of 100 USD debits the USD world account by 100 and credits the customer account by 100.
- A withdrawal does the reverse.
- The world account's balance is therefore negative, and equals minus the total money currently held in customer accounts of that currency.

It is an account in the `accounts` table with type `System`. No customer owns it, and it is the only kind of account allowed to go below zero.

## 3. Structure and technology

```
C:\UmutLedger\
├── UmutLedger.slnx
├── docker-compose.yml        Postgres for local development
├── src\UmutLedger\           the API
│   ├── Domain\               entities, value types, enums, domain exceptions
│   ├── Application\          services: accounts, transfers, queries
│   ├── Infrastructure\       EF Core context, entity mappings, migrations
│   └── Api\                  controllers, request and response models, exception handler
└── tests\UmutLedger.Tests\   xUnit + Testcontainers
```

Rules that keep the folders honest:

- `Domain` references nothing outside itself: no EF Core, no ASP.NET.
- Controllers are thin. They translate HTTP into a service call and back, and hold no business logic.
- Controllers depend on `ITransferService` and `IAccountService`, not on the concrete classes.
- Services use the EF Core context directly. The locking and the database constraints are part of the design, so they are not hidden behind repository interfaces.
- Time comes from an injected `TimeProvider`. Nothing calls `DateTime.UtcNow` directly.

Technology:

- .NET 10, ASP.NET Core controllers, OpenAPI output as in the current template.
- EF Core with the Npgsql provider and EF migrations. The row lock is the only explicit SQL; it runs through EF Core's raw SQL API so the locked entities are still tracked.
- Transactions run at PostgreSQL's default isolation level, read committed. Correctness comes from the row locks and constraints, not from a stricter isolation level.

Why not full Clean Architecture: the service has a handful of operations, the rule of real Postgres in tests removes the main benefit of repository interfaces, and every extra layer is code that must reach 100% coverage without making the ledger more correct. The README will state this reasoning.

## 4. Domain model

Every domain type is self-validating. It checks its invariants on creation and on every state change, has no public setters and no public parameterless constructor, and throws a domain exception when a rule is broken. EF Core loads entities through private constructors.

| Type | Kind | Invariants |
|---|---|---|
| `Currency` | enum | USD, EUR, GBP |
| `AccountType` | enum | Customer, System |
| `Money` | value type | Holds an amount in minor units and a currency. Refuses to add, subtract or compare different currencies |
| `Account` | entity | Name is not blank and at most 100 characters. `Debit` and `Credit` take a positive `Money` in the account's currency. `Debit` refuses to take a Customer account below zero. A System account may go negative |
| `Transfer` | entity | Amount is positive. Debit and credit accounts differ. Idempotency key is not blank and at most 255 characters. Creates its own two entries |
| `Entry` | entity | Amount is signed and never zero: negative for the debit, positive for the credit |

Ids are time-ordered UUIDs (version 7), generated in the application.

The existing `Account` class loses its `_version` field, since optimistic concurrency is not used, and its `_createdAt` becomes a `DateTimeOffset`.

## 5. Data model

Money is `bigint` in minor units. Timestamps are UTC `timestamptz`. Enums are stored as text.

### `accounts`

| Column | Type | Rules |
|---|---|---|
| `id` | uuid | primary key |
| `name` | text | not empty |
| `currency` | text | USD, EUR or GBP |
| `type` | text | `Customer` or `System` |
| `balance` | bigint | `CHECK (type = 'System' OR balance >= 0)` |
| `created_at` | timestamptz | |

A unique partial index allows only one `System` account per currency. A migration creates the three world accounts. Accounts are never deleted in this version.

### `transfers`

| Column | Type | Rules |
|---|---|---|
| `id` | uuid | primary key |
| `idempotency_key` | text | unique |
| `debit_account_id` | uuid | foreign key to `accounts` |
| `credit_account_id` | uuid | foreign key to `accounts`, `CHECK (debit_account_id <> credit_account_id)` |
| `amount` | bigint | `CHECK (amount > 0)` |
| `currency` | text | USD, EUR or GBP |
| `created_at` | timestamptz | |

There is no status column. A transfer row exists only if the transfer fully committed.

### `entries`

| Column | Type | Rules |
|---|---|---|
| `id` | uuid | primary key |
| `transfer_id` | uuid | foreign key to `transfers` |
| `account_id` | uuid | foreign key to `accounts` |
| `amount` | bigint | signed, `CHECK (amount <> 0)` |
| `created_at` | timestamptz | |

An index on `(account_id, id)` serves the history query.

### Immutability

`transfers` and `entries` are append-only. A database trigger rejects any `UPDATE` or `DELETE` on them. A mistake is corrected by a new reversing transfer.

### Ledger invariants

- All entries in each currency sum to zero.
- Every account's `balance` equals the sum of its entries.
- Every transfer has exactly two entries that sum to zero.

### Known trade-off

Every deposit and withdrawal locks the world account for its currency, so those operations run one at a time per currency. Transfers between customer accounts are not affected. This is accepted for the first version; the load-testing sub-project will measure whether it matters.

## 6. API

| Method and path | Purpose | Success |
|---|---|---|
| `POST /accounts` | Create a customer account with a name and currency. Balance starts at zero | 201 |
| `GET /accounts/{id}` | Account details and current balance | 200 |
| `GET /accounts/{id}/entries` | Entry history, newest first, paged by cursor | 200 |
| `POST /accounts/{id}/deposits` | Move money from the world account into this account | 201 |
| `POST /accounts/{id}/withdrawals` | Move money from this account to the world account | 201 |
| `POST /transfers` | Move money between two customer accounts | 201 |
| `GET /transfers/{id}` | A transfer and its two entries | 200 |

Request bodies:

- `POST /accounts`: `name`, `currency`.
- Deposits and withdrawals: `amount` in minor units, `currency`.
- `POST /transfers`: `debitAccountId`, `creditAccountId`, `amount` in minor units, `currency`.

Rules:

- The three money-moving endpoints require an `Idempotency-Key` header and all call the same `TransferService`. They share one key space, because the key is unique across the `transfers` table.
- For a deposit the server fills in the world account as the debit side; for a withdrawal, as the credit side. Callers never send system account ids.
- The request currency must match the currency of every account involved.
- System accounts can be read through the `GET` endpoints. They cannot be named in any money-moving request.
- A 201 response carries a `Location` header pointing at `GET /transfers/{id}` or `GET /accounts/{id}`.
- Entry history returns 50 entries per page by default and at most 200, set by a `limit` query parameter. The cursor is the id of the last entry returned, passed back as `cursor`. Ids are time-ordered, so this gives a stable newest-first order.

## 7. Transfer flow

All steps run inside one database transaction.

1. Validate the request: key present, amount positive, the two accounts differ.
2. Look up the idempotency key. If a transfer already exists, apply the replay rule below and stop.
3. Lock both account rows with `SELECT ... FOR UPDATE`, in ascending id order. Then look up the idempotency key again: a request that waited for these locks behind an identical request now sees that request's committed transfer, and applies the replay rule instead of executing a second time.
4. Check that both accounts exist, that neither is a system account named by the caller, and that their currency matches the request.
5. Debit and credit through the domain methods, which reject an overdraft on a customer account.
6. Insert the transfer and its two entries, save the new balances, and commit.

Any rejection rolls the transaction back completely, so a rejected request writes nothing.

### Idempotency rules

Two requests are the same when their debit account, credit account, amount and currency are all equal.

- **Same key, same request:** the original transfer is returned with status 200 instead of 201, and nothing is written.
- **Same key, different request:** rejected with `idempotency_key_reused`.
- **Two identical requests at the same instant:** both can pass step 2. They need the same account rows, so one waits for the other's locks, and its second lookup in step 3 finds the winner's transfer and replays it. This holds even when the winner's transfer left too little money to execute again.
- **Two different requests with the same key at the same instant:** if they share no account, neither waits for the other's locks. The unique constraint on `idempotency_key` lets only one commit. The loser's transaction rolls back, and it then reads the winner's transfer and applies the two rules above, which rejects it as a key reuse.
- **Rejected requests are not remembered:** a transfer that failed, for example for insufficient funds, stored nothing. Retrying the same key is evaluated again and can succeed once the account is funded.

The last rule is a deliberate simplification. Storing and replaying failed results would need a separate idempotency table that is written even when the transfer rolls back.

## 8. Error handling

Every error is an RFC 9457 problem details body (`application/problem+json`) with a stable machine-readable `code`, a human-readable `detail` and the request's trace id.

Domain classes and services throw specific domain exceptions. One exception handler turns them into responses, so controllers contain no `try/catch`. Each exception type carries its own `code` and status, so adding an error does not change the handler.

| Situation | Status | `code` |
|---|---|---|
| Missing or blank `Idempotency-Key` header | 400 | `idempotency_key_required` |
| Malformed body, amount zero or negative, unsupported currency, blank or over-long name, over-long key, invalid `limit` or `cursor` | 400 | `validation_failed` |
| Debit and credit account are the same | 400 | `same_account_transfer` |
| Account does not exist | 404 | `account_not_found` |
| Transfer does not exist | 404 | `transfer_not_found` |
| Request currency differs from an account's currency | 422 | `currency_mismatch` |
| Customer account would go below zero | 422 | `insufficient_funds` |
| A money-moving request names a system account | 422 | `system_account_not_allowed` |
| Same idempotency key, different request | 422 | `idempotency_key_reused` |
| No endpoint matches the path | 404 | `not_found` |
| The path exists but not for this HTTP method | 405 | `method_not_allowed` |
| Request body is not JSON | 415 | `unsupported_media_type` |
| Database unreachable or timed out | 503 | `service_unavailable` |
| Anything unexpected | 500 | `internal_error` |

Rules:

- Errors raised by the web framework before any ledger code runs (404, 405, 415 above) use the same problem details format. Their `code` is the standard HTTP reason phrase in snake_case, so any other framework status is covered without a lookup table.
- 400 means the request is malformed on its own. 422 means it is well-formed but the ledger's current state forbids it.
- A 503 is safe to retry, because the idempotency key guarantees a retry cannot apply twice.
- A duplicate-key race is not an error. The losing request returns the winner's transfer as a replay.
- A database `CHECK` or trigger violation is a bug, not a user error. It means a domain check was bypassed, so it returns 500 and is logged at error level.
- A 500 response contains no stack trace or SQL text, only the trace id.

## 9. Testing

`UmutLedger.Tests` uses xUnit, with Testcontainers running a real PostgreSQL. Nothing database-related is mocked.

| Level | What it covers | Uses the database |
|---|---|---|
| Domain tests | `Money`, `Account`, `Transfer`, `Entry`: every invariant and every rejection | No |
| Service tests | `TransferService` and `AccountService`: locking, idempotency, rollback | Yes |
| API tests | Each endpoint over real HTTP through `WebApplicationFactory`: status codes, problem details bodies, headers | Yes |

Database setup: one Postgres container starts per test run and the EF migrations are applied to it once, so the migrations are tested too. Tests do not reset the database. Each test creates its own accounts and idempotency keys, which keeps tests independent and lets them run in parallel.

A shared assertion checks the three ledger invariants from section 5 at the end of every money-moving test.

Concurrency tests:

- **Overdraft race:** many parallel transfers leave one account that can only afford some of them. Exactly the affordable number succeed and the balance never goes below zero.
- **Opposing transfers:** A→B and B→A run in parallel many times and all complete, proving the ordered locks prevent deadlock.
- **Duplicate key race:** the same request is fired many times at once. Exactly one transfer is written and the rest return it as a replay.

Hard-to-reach paths:

- **Database constraints and triggers:** raw SQL tries to insert invalid rows (negative customer balance, zero amount, duplicate key, second system account for a currency) and to update or delete a transfer or entry. Each must be refused.
- **503:** the API is started against an unreachable database address.
- **500:** the test host adds an endpoint that throws, to prove nothing leaks.
- **Time:** `FakeTimeProvider` controls timestamps.

Coverage gate: Coverlet measures line and branch coverage, and the test run fails below 100%. The only exclusion is the generated EF migration code.
