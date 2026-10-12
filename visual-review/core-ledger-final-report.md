# Core Ledger — Final Report

Date: 2026-10-11
Covers: the first sub-project of UmutLedger, from design to the final review.

## 1. Outcome

- All ten planned tasks are implemented, each one reviewed, and the final whole-project review judged the code **Ready**.
- `dotnet build -warnaserror`: 0 warnings, 0 errors.
- `dotnet test -p:CollectCoverage=true`: 220 of 220 tests pass; coverage is 100% line, 100% branch, 100% method. I re-ran both commands myself at the end.
- Nothing is committed. `C:\UmutLedger` is not a git repository and no git command was run.

What exists:

| Area | Contents |
|---|---|
| API | 7 endpoints: create and read accounts, entry history, deposits, withdrawals, transfers, read a transfer |
| Errors | 14 stable error codes, all returned as RFC 9457 problem details |
| Database | 3 tables, 8 check constraints, 2 unique indexes, an append-only trigger, 2 EF migrations |
| Tests | 220 tests across domain, database, service, concurrency and HTTP levels |
| Docs | Spec, plan, `README.md`, sample requests in `src/UmutLedger/UmutLedger.http` |

## 2. How the work was done

| Stage | What happened |
|---|---|
| Design | 6 questions answered one at a time, then 5 design sections approved one at a time |
| Spec | Written to `docs/superpowers/specs/2026-10-11-core-ledger-design.md`, reviewed in the browser, approved |
| Plan | 10 test-first tasks written to `docs/superpowers/plans/2026-10-11-core-ledger.md`, reviewed in the browser, approved |
| Execution | Subagent-driven: a fresh implementer per task, a fresh reviewer per task, a scoped re-review after every fix |
| Final review | One whole-project review on the most capable model, one fix wave, one re-review |

In total 10 task implementations, 10 task reviews, 5 fix rounds with 4 scoped re-reviews, then 1 final review, 1 final fix wave and 1 final re-review.

## 3. Development choices

### 3.1 Choices you made during design

| Question | Your choice | Alternatives turned down |
|---|---|---|
| What is the project for? | A portfolio piece | A learning exercise; the foundation of a real product |
| How is money movement recorded? | Double-entry ledger: two immutable entries per transfer that sum to zero | Storing only balances |
| How does money enter an account? | One system "world" account per currency; deposits and withdrawals are ordinary transfers against it | Opening balances set directly |
| Cross-currency transfers? | Same currency only; mismatches rejected | A fixed rate table with exchange accounts |
| Authentication? | None, stated as a deliberate boundary | An API key per client |
| How do concurrent transfers stay correct? | Pessimistic row locks (`SELECT ... FOR UPDATE`) taken in ascending account id order | Optimistic concurrency with retries; serializable isolation |
| Full Clean Architecture? | No. One API project with `Domain`, `Application`, `Infrastructure`, `Api` folders; services use the EF Core context directly | A project per layer with repository interfaces and a mediator |
| Coding rules | Clean code, SOLID, and every class self-validating, added to `CLAUDE.md` at your request | — |
| Sub-project scope | Core ledger only; observability, Docker image and Kubernetes, load tests and authentication are later sub-projects | One large spec |

### 3.2 Choices I made in the spec, which you confirmed

| Choice | Why |
|---|---|
| Money is a `long` in minor units; no decimals or floats | Removes rounding as a source of error |
| Ids are time-ordered UUIDs (version 7) | Gives a stable newest-first order for paging without a sequence |
| No status column on `transfers` | A transfer row exists only if it fully committed |
| `transfers` and `entries` are append-only, enforced by a trigger | History cannot be rewritten even by a bug |
| Deposits, withdrawals and transfers share one idempotency key space | One unique constraint; a key reused across endpoints is detected |
| A rejected transfer is not remembered | Storing failed results needs a separate table written even on rollback |
| System accounts are readable but can never be named in a money-moving request | The world balance is useful to see; callers must not move it directly |
| Limits: names 100 characters, keys 255, history 50 per page and 200 at most | Sensible bounds; you confirmed them |
| 400 for a malformed request, 422 for one the ledger's state forbids | Lets a client tell "fix your request" from "not possible right now" |
| Read committed isolation | Correctness comes from the row locks and constraints, not a stricter level |
| Logging is the built-in `ILogger` to the console for now | Traces, metrics and dashboards belong to the observability sub-project |

### 3.3 Choices I made in the plan

| Choice | Why |
|---|---|
| Exceptions carry a kind (invalid request, not found, rule violation), not an HTTP status | Keeps `Domain` free of HTTP; the API maps kind to 400, 404, 422 |
| `Money` is a record class, not a struct | A struct always has a default value, which would be an invalid `Money` |
| `Transfer.Execute` debits, credits and creates both entries in one domain method | The money rule lives in one place and is testable without a database |
| EF Core binds the entities' own validating constructors | Loaded rows are re-validated; no public setters or parameterless constructors are needed |
| One PostgreSQL container for the whole test run; tests never reset it | Tests stay independent and run in parallel |
| Tests never change a balance without holding its row lock | The world accounts are shared by every test |
| Two forced-interleaving tests poll `pg_blocking_pids` | Makes a race deterministic instead of relying on timing |
| xUnit v2 with a static shared container | Lets test classes run in parallel without a collection fixture |
| Coverage is only collected with `-p:CollectCoverage=true` | A single filtered test run is not failed by the 100% threshold |
| The request trace id is ASP.NET's request identifier | Reading the OpenTelemetry id would add a branch no test can reach yet |
| Amounts that would overflow a balance are refused with `validation_failed` | The spec did not cover it; a crash would be worse |
| A local `dotnet-ef` tool, not a global install | Keeps your machine's global tools unchanged |
| No git steps anywhere in the plan | Your rules: you commit yourself |

### 3.4 Rulings I made during execution

These are decisions taken on your behalf while the plan ran. Each is recorded in the ledger at `.superpowers/sdd/2026-10-11-core-ledger/progress.md`.

| # | Ruling | Cost if I was wrong |
|---|---|---|
| 1 | No worktree, branch or commits; reviews compared before and after snapshots of the source instead of commits | None; you commit yourself |
| 2 | Kept `EnsurePositive_Positive_DoesNotThrow`, a test with no `Assert` | One weak test |
| 3 | Added null guards beyond the plan (Task 3) | A few redundant checks |
| 4 | Accepted the extra `Microsoft.EntityFrameworkCore.Relational` package reference (Task 4) | A version pin that can drift |
| 5 | Changed the transfer flow against the approved plan: a second key lookup after locking (Task 6); spec section 7 amended | One extra indexed read per new transfer |
| 6 | Moved two concurrency tests from Task 7 into the Task 6 fix so the fix was test-driven; plan amended | Task 7 differs from the text you approved |
| 7 | Added three error codes not in the approved spec: `not_found`, `method_not_allowed`, `unsupported_media_type` (Task 8); spec table extended | Clients may rely on codes you did not choose |
| 8 | Added 22 HTTP tests for error paths the plan had not covered (Task 9); did not add per-route 405 tests | A little extra test run time |
| 9 | Skipped a separate re-review of the one-paragraph README fix; the final reviewer checked it instead | An inaccurate sentence could have survived until the final review (it did not) |
| 10 | Fixed five of the final review's eleven minor findings in one wave | Extra code on the money path that itself had to be correct; it was re-reviewed |
| 11 | Left the coverage gate behind the `-p:CollectCoverage=true` flag | A plain `dotnet test` in CI would not enforce coverage |
| 12 | Left six other minor findings and three re-review residuals unfixed (section 6) | Each is a small hardening you may want before production |
| 13 | Kept the `.superpowers` workspace instead of deleting it | A leftover folder you can delete |

## 4. Errors and how they were fixed

### 4.1 Defects in the product, caught by review

**D1. Idempotency breach in the transfer flow (Task 6, Critical).**
- Where it came from: my spec and plan. The implementer built exactly what I wrote.
- What was wrong: two identical requests with the same key arrive together. Both pass the key lookup. The first locks the accounts, moves the money and commits. The second was waiting for the locks; when it woke it executed the transfer again. If the first had drained the account, the second returned `insufficient_funds` for a transfer that had succeeded. A client trusting that error could retry with a new key and pay twice.
- How it was found: the Task 6 reviewer reasoned through PostgreSQL's read-committed semantics.
- Fix: the service looks the key up a second time once it holds the locks (`FindReplayAsync`, called before and after `LockAccountsAsync`).
- Proof: a new deterministic test failed with `InsufficientFundsException` before the fix and passes after. A second new test covers the unique-constraint path, which the first plan left untested. Both passed five repeat runs.
- Follow-up: spec section 7 and plan Tasks 6 and 7 amended. A scoped re-review walked every interleaving (same or different request; sharing both accounts, one, or none) and found none left that gives a wrong answer.

**D2. A failed transfer could leave a debit that a later save would persist (final review, Minor).**
- What was wrong: `Transfer.Execute` debited, then credited. If the credit threw, the debit account stayed debited in memory. Nothing saved it in normal use, but a later save on the same database context would.
- Proof: a test on one reused context showed the stored balance at 90 instead of 100.
- Fix: `Account.EnsureCanCredit` validates the credit before any change, and the service clears tracked entities at the start of each transfer.

**D3. Null inputs crashed instead of returning a ledger error (Task 3 and final review).**
- What was wrong: a null idempotency key was accepted by `Transfer`; a null account or a null `Money` threw `NullReferenceException`.
- Fix: a `Require.NotNull` helper and guards in `Transfer.Execute`, `Account.Debit`, `Account.Credit` and `Account.EnsureCanCredit`, with one test per guard.

**D4. Framework errors were not coded problem details (Task 8, Important).**
- What was wrong: the spec says every error is a problem details body with a `code`. An unknown route and a wrong method returned an empty body; a non-JSON body returned a body with no `code`.
- Fix: `StatusCodeProblem` plus `UseStatusCodePages`, and `SuppressMapClientErrors`. The code is the HTTP reason phrase in snake_case, so there is no lookup table.
- Proof: three tests failed before the fix and pass after.

**D5. A transfer's entries could come back in a different order (final review, Minor).**
- What was wrong: both entry ids share one millisecond and the query had no ordering, so a replay could list credit first where the original listed debit first.
- Fix: `TransferResponse.From` orders by amount, so the debit is always first.
- Proof: the new test failed in 3 of 6 runs before the fix and passed 8 of 8 after.

**D6. The README said something untrue (Task 10, Important).**
- What was wrong: it said that when two different requests share a key, the loser "returns the winner's transfer". The code rejects it with `idempotency_key_reused`.
- Fix: the paragraph now states both cases separately. The final reviewer confirmed it against the service code.

**D7. Error paths were under-tested (Task 9, Important).**
- What was wrong: no product defect, but withdrawals had only two cases tested, `POST /transfers` had no invalid-body test, and three variants were not pinned.
- Fix: 22 HTTP tests added. All passed on the first run, so the behaviour was already correct.

**D8. Smaller items fixed in the final wave.**
- The spec says every money-moving test ends with the ledger invariant check; 43 tests did not. Added.
- The currency list `USD, EUR, GBP` was written by hand in two database checks. It is now built from the `Currency` enum, with the SQL text unchanged so no migration was needed.
- A trailing space in the validation error text, an inaccurate code comment, and a byte-order mark at the start of `UmutLedger.http`.

### 4.2 Build and environment errors

| Error | Cause | Fix |
|---|---|---|
| `CS1705` and `MSB3277` when the test project built (Task 4) | The test project resolved EF Core 10.0.4 through Npgsql while the API used 10.0.12 | A direct `Microsoft.EntityFrameworkCore.Relational` 10.0.12 reference in `UmutLedger.csproj` |
| `StatusCodeContext` not found (Task 8 fix) | Missing `using Microsoft.AspNetCore.Diagnostics` | Added the using |
| Docker daemon not running | Docker Desktop was installed but not started | I started Docker Desktop before Task 4 |
| `dotnet ef` not found | The tool was not installed | Installed as a local tool; the manifest is `dotnet-tools.json` at the repo root |
| `dotnet test --no-incremental` rejected (Task 7) | It is not a valid switch for `dotnet test` | The warning count was taken from `dotnet build --no-incremental` |

### 4.3 Mistakes in my own process

| Mistake | Effect | What I did |
|---|---|---|
| The plan's first transfer flow had the idempotency gap (D1) | The most serious defect of the project | Accepted the finding, fixed it test-first, amended the spec and plan, told you it was mine |
| The plan missed framework errors, null guards and several error-path tests | Four fix rounds | Accepted each finding and ruled against my own plan text |
| I miscounted the expected test totals in four plan tasks | Would have confused the implementers | Recounted and corrected before execution |
| The plan used an xUnit `TheoryData` of exception objects | Would have raised an analyzer warning and failed the warnings-as-errors build | Rewrote it as one `Fact` before execution |
| The execution skill's helper scripts need a git repository | They failed with `fatal: not a git repository` | Wrote three small scripts that brief, snapshot and diff without git |
| A shell command of mine had a quoting error | Nothing ran; the ledger entry and fix instructions were not written | Rewrote the files with the file tools and re-sent |
| My diff script merged git's line-ending warnings into the Task 6 review diff | The reviewer noticed corrupted lines | Fixed the script to discard that output |
| A subagent stopped the smoke-run API by killing every process matching "UmutLedger" and "dotnet" | It may have stopped Visual Studio or another `dotnet` process on this solution | Reported to you as soon as I saw it |

## 5. Timeline of the ten tasks

| Task | Result | Fix rounds | Tests after |
|---|---|---|---|
| 1. Test project, domain errors, `Money` | No deviations; review clean | 0 | 16 |
| 2. `Account` entity | No deviations; review clean | 0 | 36 |
| 3. `IdempotencyKey`, `Entry`, `Transfer` | Review found null guards missing (D3) | 1 | 61 |
| 4. Persistence and migrations | One deviation: the EF Core package reference; review clean | 0 | 78 |
| 5. `AccountService` and paging | No deviations; review clean | 0 | 95 |
| 6. `TransferService` and idempotency | Review found the idempotency breach (D1) | 1 | 125 |
| 7. Concurrency proofs | No deviations; five repeat runs stable; review clean | 0 | 128 |
| 8. Error handling and accounts API | Review found uncoded framework errors (D4) | 1 | 159 |
| 9. Transfers API | Review found under-tested error paths (D7) | 1 | 208 |
| 10. Coverage gate and README | Coverage was 100% on the first measurement; review found the README sentence (D6) | 1 | 208 |
| Final review fix wave | Five minor findings fixed (D2, D3, D5, D8) | — | 220 |

## 6. What is left open

Left deliberately, for you to decide:

- **Coverage gate:** it only runs with `-p:CollectCoverage=true`. A plain `dotnet test` passes at any coverage.
- **`TRUNCATE`:** it bypasses the append-only trigger. The spec only named `UPDATE` and `DELETE`.
- **Untested guard:** no test fails if `db.ChangeTracker.Clear()` is removed from `TransferService`, because the domain fix alone makes the reused-context test pass.
- **Null `Money` at the service boundary:** it still gives `NullReferenceException`. Controllers always build a `Money`, so it is not reachable over HTTP.
- **Pre-1970 timestamp:** `Transfer.Execute` would throw a non-ledger exception after updating both accounts. It is not reachable through the service.
- **Production configuration:** `appsettings.json` has no `Ledger` connection string, so the API only runs in Development. This belongs with the deployment sub-project.
- **Row-lock SQL:** it is repeated in two test helpers, so a change to the production lock query would not reach them.
- **`EntryPage.From`:** it does not validate its own `limit`. The only caller passes a validated one.
- **Trace id:** it is ASP.NET's request identifier until the observability work.
- **Lock-order test:** the opposing-transfers test would probably still pass if `ORDER BY` were removed from the lock query.
- **Naming nits:** two infrastructure error codes live in `Domain`; `LedgerConstraints.ConnectionStringName` is not a constraint; three EF-default index names are not snake_case.

Not verified:

- The final reviewer read the code but ran nothing; the test evidence is the gated run.
- No load testing has been done. The known trade-off stands: deposits and withdrawals in one currency queue on that currency's world account.

## 7. Where things are

| Item | Path |
|---|---|
| Spec | `docs/superpowers/specs/2026-10-11-core-ledger-design.md` |
| Plan | `docs/superpowers/plans/2026-10-11-core-ledger.md` |
| Execution ledger, task reports, review diffs | `.superpowers/sdd/2026-10-11-core-ledger/` |
| README | `README.md` |
| This report | `visual-review/core-ledger-final-report.md` |

Next sub-project in the agreed order: observability (OpenTelemetry, Prometheus, Grafana).
