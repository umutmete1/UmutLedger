# Load and Chaos Test — Results

Date: 2026-10-12
Covers: two throwaway test runs against the UmutLedger API on this machine, watched through the new Grafana stack.

## 1. Summary

- **Money stayed correct.** After 497,416 requests, including a database outage, every ledger invariant holds across all 355,748 transfers.
- **No request got a 500.** Every server-side failure was answered as a 503 "retry with the same idempotency key". Every client mistake got the 4xx it should.
- **One real weakness was found.** The API's connection pool can grow to 100 connections and PostgreSQL accepts 100 in total. Under contention the API hit that ceiling (5,437 "too many clients" errors), and after the run it held every connection, locking out `psql` and DataGrip.
- **Throughput:** with 32 callers, about 1,500 transfer requests were completed per second and about 285 deposit requests per second. Deposits are slower because deposits in one currency queue on that currency's world account.
- **Two phases of the chaos run did not test what they were meant to.** Phases 4 and 5 were dominated by "Failed to connect to 127.0.0.1:5432" errors whose cause I did not establish.

## 2. How the tests were run

| Item | Value |
|---|---|
| Machine | This Windows 11 machine; API and load generator on the host, PostgreSQL and the monitoring stack in Docker Desktop |
| API | Release build, Development environment, one process for both runs, `http://localhost:5289` |
| Database | PostgreSQL 17 in Docker, `max_connections` 100 |
| Connection pool | Npgsql default: up to 100 connections |
| Load generator | Two single-file C# programs in my scratch folder, not in the repository |
| Telemetry | Every request traced; metrics exported every 5 seconds; all logs sent to Loki |
| Started | Load run 13:49:24 local time (10:49:24 UTC); chaos run 13:52:50 |

Where the numbers come from, and how far to trust them:

- **Request counts per phase** are what the load generator counted from the responses it received. These are exact.
- **Latencies, lock waits, connection counts** come from Prometheus. Each phase's window is padded by 6 seconds because metrics are exported every 5 seconds, so neighbouring phases bleed into each other slightly.
- **Server-side counts from Prometheus** (movements by outcome, HTTP status) are estimates for the same reason, and are too high for very short phases. Where they disagree with the load generator, the load generator is right.
- **Error causes** come from Loki, counted over each phase's exact window.
- **Ledger correctness** comes from SQL queries run directly against the database after the run.

### 2.1 How to read p50, p95 and p99

Requests do not all take the same time, so one average hides the slow ones. A percentile says how long the slower requests took.

Line up every request of a stage from fastest to slowest:

| Name | Meaning | Example: deposits |
|---|---|---|
| p50 (the median) | Half of the requests were faster than this, half slower. The typical request | 20 ms: a typical deposit took 20 ms |
| p95 | 95 out of 100 requests were faster than this. 1 in 20 was slower | 756 ms: 1 deposit in 20 took longer than three quarters of a second |
| p99 | 99 out of 100 requests were faster than this. 1 in 100 was slower | 2.16 seconds: 1 deposit in 100 took longer than 2 seconds |

- A large gap between p50 and p99 means most requests are fine but some wait a long time, which usually points at queueing.
- p50 and p99 close together means every request takes about the same time.
- The same reading applies to "lock wait" (time spent waiting for the account locks) and "SQL command" (time of one database command).
- These values are estimates. The API counts requests into time ranges (up to 5 ms, up to 10 ms, and so on) and Prometheus works the percentile out from those counts.

## 3. Run 1: load

Scenario: create 20,000 accounts (60% USD, 25% EUR, 15% GBP), deposit 1000.00 into each, then send 100,000 transfer requests between random accounts with random amounts from 1.00 to 400.00. About 2% of transfers repeat the same idempotency key, and about 1% go to an account in the wrong currency. 32 requests at a time.

### 3.1 Totals

140,000 requests in 2 minutes 22 seconds: 982 per second overall. No server errors, no client-side failures.

| Request | Response | Count |
|---|---|---|
| Create account | 201 | 20,000 |
| Deposit | 201 | 20,000 |
| Transfer | 201 created | 92,594 |
| Transfer | 422 rejected | 5,495 |
| Same key sent again | 200 replayed | 1,799 |
| Same key sent again | 422 | 112 |

- The 422 rejections split into about 4,700 `insufficient_funds` and about 1,000 `currency_mismatch` (Prometheus estimate).
- The 112 repeats that got 422 are repeats of transfers that were themselves rejected. A rejected transfer writes nothing, so its key is not stored and the repeat is evaluated again. This is the designed behaviour.

### 3.2 By stage

Each stage sent all of its requests with 32 callers working at the same time. "Whole stage took" is the time from the stage's first request to its last, not the time of one request.

| Stage | Requests sent | Whole stage took | Requests completed per second | Callers at once |
|---|---|---|---|---|
| Create accounts | 20,000 | 5 seconds | about 4,000 | 32 |
| Deposits | 20,000 | 70 seconds | about 285 | 32 |
| Transfers | 100,000 | 67 seconds | about 1,500 | 32 |

How long one request took, from arriving at the API to its response leaving (see section 2.1 for what p50, p95 and p99 mean):

| Stage | Average (calculated) | Half were faster than (p50) | 95% were faster than (p95) | 99% were faster than (p99) |
|---|---|---|---|---|
| Create accounts | about 8 ms | 6 ms | 10 ms | 18 ms |
| Deposits | about 112 ms | 20 ms | 756 ms | 2.16 seconds |
| Transfers | about 21 ms | 19 ms | 42 ms | 48 ms |

The average is calculated, not measured: 32 callers divided by the requests completed per second. So one deposit took about a tenth of a second on average, and the slowest 1% took more than 2 seconds. No single deposit took 70 seconds.

Where a request's time went:

| Stage | Waiting for the account locks: p50 | p95 | p99 | One SQL command, p95 | Database connections in use, peak |
|---|---|---|---|---|---|
| Deposits | 9 ms | 783 ms | 2.16 seconds | 25 ms | 32 |
| Transfers | 2 ms | 5 ms | 9 ms | 7 ms | 32 |

**The deposit bottleneck is real and measured.**

- Every deposit in one currency locks that currency's world account, so deposits in the same currency run one after another, never side by side.
- With 32 callers sending deposits, most of them are waiting at any moment. That is why 285 deposit requests finish per second while 1,500 transfer requests do: transfers between different customers lock different accounts and run side by side.
- The slowest 1% of deposits took 2.16 seconds, and the slowest 1% of lock waits for deposits was also 2.16 seconds. The time is spent queueing for the lock, not doing work.
- Half of all deposits still finished within 20 ms. The queue is uneven: a deposit that arrives when the lock is free is fast, one that arrives behind a burst waits.
- This is the known trade-off from the core-ledger design, now with numbers.

Money moved in this run (Prometheus estimate, minor units): deposits about 2.0 billion (20,000,000.00 across currencies, which matches 20,000 accounts at 1000.00), transfers about 1.86 billion.

## 4. Run 2: chaos

Setup: 400 new USD accounts with 10,000.00 each. Then eight phases. Phases 1 to 6 and 8 keep 16 callers sending ordinary random transfers in the background ("baseline"); phase 7 replaces them with 800 callers.

### 4.1 Results per phase

Counts are responses seen by the load generator.

| # | Phase | Duration | 201 | 4xx | 503 | Client gave up | Connection failed |
|---|---|---|---|---|---|---|---|
| 1 | Calm | 40 s | 52,525 | 0 | 0 | 0 | 0 |
| 2 | Bad requests | 40 s | 41,890 | 57,540 | 0 | 0 | 0 |
| 3 | Hot accounts | 41 s | 3,412 | 0 | 6,805 | 0 | 0 |
| 4 | Impatient clients | 40 s | 129 | 0 | 6,019 | 19,026 | 6,794 |
| 5 | Killed connections | 42 s | 1,385 | 0 | 17,321 | 0 | 0 |
| 6 | Database outage | 60 s | 40,323 | 0 | 446 | 0 | 0 |
| 7 | Overload | 41 s | 56,586 | 0 | 0 | 0 | 0 |
| 8 | Calm again | 40 s | 46,415 | 0 | 0 | 0 | 0 |

Totals for the chaos run: 357,416 requests including the 800 setup requests; 30,591 answered 503.

### 4.2 Measurements per phase

| # | Phase | Request p50 | p95 | p99 | Transfer lock wait p99 | SQL p95 | Connections in use, peak |
|---|---|---|---|---|---|---|---|
| 1 | Calm | 16 ms | 24 ms | 25 ms | 10 ms | 5 ms | 16 |
| 2 | Bad requests | 12 ms | 44 ms | 69 ms | 23 ms | 7 ms | 30 |
| 3 | Hot accounts | 368 ms | 2.77 s | 4.98 s | 8.21 s | 1.61 s | 100 |
| 4 | Impatient clients | 145 ms | 243 ms | 374 ms | 166 ms | 37 ms | 89 |
| 5 | Killed connections | 30 ms | 67 ms | 95 ms | 19 ms | 5 ms | 16 |
| 6 | Database outage | 18 ms | 621 ms | 729 ms | 94 ms | 9 ms | 55 |
| 7 | Overload | 565 ms | 737 ms | 848 ms | 212 ms | 43 ms | 100 |
| 8 | Calm again | 17 ms | 24 ms | 35 ms | 12 ms | 5 ms | 16 in use, 100 held open |

### 4.3 Phase by phase

**Phase 1, calm.** 16 callers, 1,313 transfers per second, p99 25 ms, no errors. This is the baseline.

**Phase 2, bad requests.** Ten kinds of client mistake, sent by 16 extra callers. All were refused with the right status, and the baseline kept running at p99 69 ms.

| Mistake | Status | Code | How it shows up |
|---|---|---|---|
| Deposit without `Idempotency-Key` | 400 | `idempotency_key_required` | HTTP metrics only |
| Deposit of amount 0 | 400 | `validation_failed` | `ledger.movements` |
| Transfer from an account to itself | 400 | `same_account_transfer` | `ledger.movements` |
| Malformed JSON | 400 | not checked in this run | HTTP metrics only |
| Deposit to an account that does not exist | 404 | `account_not_found` | `ledger.movements` |
| Unknown route | 404 | `not_found` | HTTP metrics only |
| `DELETE /accounts` | 405 | `method_not_allowed` | HTTP metrics only |
| Body sent as `text/plain` | 415 | `unsupported_media_type` | HTTP metrics only |
| Withdrawal far above the balance | 422 | `insufficient_funds` | `ledger.movements` |
| Deposit in EUR to a USD account | 422 | `currency_mismatch` | `ledger.movements` |

Totals: 23,004 × 400, 11,502 × 404, 5,847 × 405, 5,692 × 415, 11,495 × 422. The split between "HTTP metrics only" and `ledger.movements` is the known limit from the observability spec, seen in practice.

**Phase 3, hot accounts.** 150 callers transferring 0.01 back and forth between the same two accounts.

- The lock did its job: transfers queued, and the transfer lock wait rose to p50 420 ms, p95 4.48 s, p99 8.21 s.
- Every waiting transfer holds a database connection while it waits. Connections in use reached 100, the pool's maximum and also PostgreSQL's maximum.
- Past that point new connections were refused. Loki shows 5,437 errors "53300: sorry, too many clients already" and 768 "Failed to connect to 127.0.0.1:5432". These became the 6,805 responses with status 503.
- The baseline callers on other accounts suffered too: only 3,412 transfers succeeded in the whole phase.

**Phase 4, impatient clients.** 32 callers cancel their request after 15 ms, while 60 callers keep the hot accounts busy.

- 19,026 requests were abandoned by the client, and 6,794 failed on the client side before a response.
- On the server only about 150 were recorded as `cancelled` movements, and about 160 as HTTP status 499. Most clients gave up before their request reached the transfer service.
- 6,019 requests got 503. Loki shows 5,908 errors "Failed to connect to 127.0.0.1:5432" in this phase, with 89 connections in use, below the limit.
- I did not establish why connecting failed here. The 57 errors mentioning a cancelled Windows socket lookup suggest the machine's network stack was under strain from thousands of aborted connections, but that is a guess.

**Phase 5, killed connections.** Every 4 seconds a SQL command asked PostgreSQL to terminate all other connections.

- 17,321 of 18,706 requests got 503.
- Loki shows 17,423 errors, all "Failed to connect to 127.0.0.1:5432". Only 2 errors in the whole run say a connection was terminated by the administrator command, and they fall in phase 6's window.
- So this phase mostly measured the same connect failures as phase 4, not what happens when connections are killed. The phase is inconclusive.

**Phase 6, database outage.** PostgreSQL was stopped at 13:56:23 and started at 13:56:49.

- Only 446 requests got 503 during 26 seconds of downtime. Callers did not fail fast: each one waited on its connection attempt, so few requests were made at all.
- Error causes: 319 "Exception while reading from stream", 58 "the database system is shutting down", 50 transient-failure wrappers from Entity Framework, 39 failed connects, 3 uses of an already disposed transaction, 2 administrator terminations. All of them were answered 503.
- Recovery needed no restart of the API. In the same 60-second phase 40,323 transfers succeeded, almost all after PostgreSQL came back.

**Phase 7, overload.** 800 callers sending ordinary transfers.

- No errors. 56,586 transfers in 41 seconds, about 1,380 per second, the same throughput as with 16 callers.
- The cost was latency: p50 rose from 16 ms to 565 ms. Requests queued for one of the 100 pooled connections.
- All 100 connections were in use, yet nothing was refused. Unlike phase 3, connections were returned quickly, so the pool never needed more than PostgreSQL could give.

**Phase 8, calm again.** Back to 16 callers: p99 35 ms, no errors. The API recovered fully on its own. It kept all 100 connections open and idle, which is what locked out other database clients afterwards.

### 4.4 Outcomes recorded by the ledger metrics, both runs

Prometheus estimates over the whole period.

| Outcome | Count |
|---|---|
| `created` | about 357,000 |
| `error` (answered 503) | about 30,800 |
| `insufficient_funds` | about 10,100 |
| `currency_mismatch` | about 6,400 |
| `account_not_found` | about 5,500 |
| `validation_failed` | about 5,400 |
| `same_account_transfer` | about 5,300 |
| `replayed` | about 1,800 |
| `cancelled` | about 130 |

Lock waits recorded: about 340,700 for transfers, 31,800 for deposits, 5,400 for withdrawals.

## 5. Ledger correctness after both runs

Checked with SQL directly against the database, after stopping the API.

| Check | Result |
|---|---|
| Accounts | 20,419 |
| Transfers | 355,748 |
| Entries | 711,496 (exactly 2 per transfer) |
| Transfers whose entries do not sum to zero | 0 |
| Transfers without exactly 2 entries | 0 |
| Accounts whose balance differs from the sum of their entries | 0 |
| Customer accounts with a negative balance | 0 |
| Idempotency keys that appear more than once | 0 |
| Total of all balances, EUR | 0 |
| Total of all balances, GBP | 0 |
| Total of all balances, USD | 0 |

Reconciliation of the counts:

- Before the runs: 19 accounts and 88 transfers (your demo data and the smoke tests).
- Load run: 20,000 accounts; 20,000 deposits and 92,594 transfers written. 88 + 112,594 = 112,682, which is what the database held after the load run.
- Chaos run: 400 accounts; 243,066 transfers written. 112,682 + 243,066 = 355,748. It matches.

**One transfer committed without its caller seeing success.** The load generator counted 243,065 successful movements in the chaos run; the database holds 243,066. One caller got a 503, a cancellation or a broken connection for a transfer that had in fact committed. This is the situation idempotency keys exist for: sending the same request again with the same key returns the original transfer and moves no more money. The load generator does not retry, so the difference remained visible.

## 6. What the telemetry showed about itself

| Item | Value |
|---|---|
| Log lines sent to Loki in the chaos run's 15 minutes | about 1.4 million information, 60,109 error, 1 warning |
| Log lines per successful transfer | about 5 |
| Error log lines per failed request | 2 |
| Garbage collections over both runs | 789 gen 0, 1,453 gen 1, 107 gen 2 |
| Largest heap after a collection | about 165 MB |
| Traces | Searchable in Tempo by outcome; I checked that a search for `error` outcomes returns traces, not how many were stored |

- **Most log lines are not ours.** Entity Framework logs every SQL command at information level in the Development settings. One transfer runs several commands, so the movement log line is about one line in five.
- **Each failed request is logged twice at error level:** once by Entity Framework's connection logger and once by the API's exception handler.
- **20 cancelled requests were logged as unhandled errors** by ASP.NET Core's exception middleware in phase 4, although a client hanging up is not a fault.
- The Collector, Prometheus, Tempo and Loki kept up. I saw no dropped-data errors, but I did not look for them systematically.

## 7. Findings

| # | Finding | Severity | Evidence |
|---|---|---|---|
| 1 | The connection pool's maximum (100) equals PostgreSQL's `max_connections` (100). Under lock contention the API exhausts the database and locks out every other client, itself included | High | Phase 3: 100 connections in use, 5,437 "too many clients"; after the run `psql` was refused until the API was stopped |
| 2 | A hot account turns one account's contention into an outage for everyone. Callers on unrelated accounts failed too, because waiting transfers hold connections | High | Phase 3: baseline dropped from 52,525 to 3,412 successes |
| 3 | Deposits and withdrawals in one currency are serialised by the world account | Medium, known | Load run: 285 deposit requests completed per second against 1,500 transfer requests per second, both with 32 callers; the slowest 1% of deposits took over 2.16 seconds |
| 4 | During a database outage requests hang on the connection attempt and do not fail fast | Medium | Phase 6: 446 responses with 503 in 26 seconds |
| 5 | Log volume in Development is dominated by Entity Framework's SQL command logs | Low | About 5 information lines per transfer; 1.4 million lines in 15 minutes |
| 6 | Each database failure is logged twice at error level | Low | Phases 3 to 6: two error lines per 503 |
| 7 | A request cancelled by the client can be logged as an unhandled error | Low | Phase 4: 20 such lines |
| 8 | The request latency histogram tops out at 10 seconds, so worse latencies are invisible | Low | Request p99 read exactly 10 s at one point |
| 9 | The "Rejections by error code" panel hides `error` outcomes, so 30,800 failures show only on the outcome and 5xx panels | Low | Dashboard filter excludes `error` by design |

What held up:

- No 500 responses. Every database failure, including a disposed transaction and a server shutdown mid-request, was mapped to 503.
- No wrong money, no duplicate transfer, no partial write (section 5).
- Full recovery after the outage and after the overload without restarting the API.
- 800 concurrent callers with zero errors.

### 7.1 The connection limit, explained

**What a connection is.** To run a transfer the API must talk to PostgreSQL over a database connection. One connection serves one request at a time, and a transfer keeps its connection from the start of its database transaction to the end.

**Two separate limits, both set to 100.**

| Limit | Who enforces it | Value | What happens at the limit |
|---|---|---|---|
| Connection pool maximum | The API's database driver | 100 (its default; the project does not set it) | The next request waits in line for a connection to come back |
| `max_connections` | PostgreSQL | 100 (its default; the project does not set it) | The next connection attempt is refused with "too many clients already" |

The pool's limit counts only the API's connections. PostgreSQL's limit counts everyone's: the API, DataGrip, `psql`, any other tool. So whenever anything else is connected, the API can never actually get its 100th connection, and it does not know that.

**Why it normally does not matter.** A transfer takes about 20 ms. With 16 callers, at most 16 transfers are in progress, so 16 connections are enough. Phase 1 used 16.

**What went wrong in phase 3, step by step.**

1. 150 callers all sent transfers between the same two accounts.
2. A transfer must lock both account rows. Only one transfer can hold those locks at a time; the other 149 wait.
3. A waiting transfer has already opened its transaction, so it holds its connection for the whole wait. The waits were long: 420 ms typically, over 8 seconds for the slowest 1%.
4. So the number of connections in use is no longer "transfers doing work" but "transfers standing in the queue". It climbed to 100.
5. While the pool was still below its own maximum, each new request made it open one more connection. PostgreSQL had no slots left and refused: 5,437 times "53300: sorry, too many clients already", plus 768 failed connection attempts.
6. The API answered each refused request with a 503. The refusal comes back at once, so the same caller tried again immediately and was refused again. That is how 6,805 requests failed in 41 seconds.
7. The 16 baseline callers, working on completely different accounts, needed connections too and were refused the same way. Contention on two accounts became a failure for everyone.

**Why 800 callers in phase 7 caused no errors.** Their transfers were on different accounts and finished in milliseconds, so connections came back to the pool quickly. The pool reached 100 and the other 700 callers waited in the pool's own line, which is the designed behaviour. Nothing was refused. I believe PostgreSQL also had all 100 slots free for the API at that point, because the restart in phase 6 had disconnected every other client, but I did not record who was connected, so that part is my reading and not a measurement.

**Why the database was locked out afterwards.** A pool keeps its connections open after use so the next request does not pay to open one. After phase 7 the API sat idle holding all 100. When I ran `psql` it was refused with "too many clients already" until I stopped the API. DataGrip would have been refused the same way. This part I observed directly.

**What would address it.** These are options, not decisions:

- Set the pool's maximum well below PostgreSQL's limit, so the API queues requests in its own line and never asks for a connection PostgreSQL cannot give.
- Raise PostgreSQL's `max_connections`, which costs memory per connection.
- Put a time limit on waiting for a lock, so a transfer stuck behind a hot account gives its connection back and fails with a clear error.
- With several copies of the API (the Kubernetes sub-project), the limits must be shared out: copies × pool maximum has to stay below PostgreSQL's limit.

## 8. Limits of this test

- **One machine.** The load generator, the API, PostgreSQL and five monitoring containers shared the same CPU and network stack. The throughput numbers describe this machine, not the application's ceiling.
- **Phases 4 and 5 are inconclusive.** Their failures were connect errors I did not explain. They may be an effect of the test itself: thousands of aborted HTTP connections on one Windows machine.
- **The phases were not isolated.** Each ran straight after the previous one, on the same API process and connection pool, so one phase's damage carried into the next.
- **Short phases.** 40 seconds each, with metrics exported every 5 seconds. Per-phase Prometheus numbers are approximate.
- **No retries.** The load generator never retried a failed request, so the test did not exercise recovery through idempotent replay after a 503.
- **One run.** Nothing here was repeated, so I cannot say how much any number varies.
- **Throwaway tooling.** The scripts are not in the repository and were not reviewed. The agreed plan has a proper load-testing sub-project (NBomber or k6) later.

## 9. State afterwards

- The API is stopped. I stopped it to free the database connections.
- The six containers are running.
- The database holds the test data on top of your demo data: 20,419 accounts, 355,748 transfers. `docker compose down -v` would delete it together with everything else.
- `observability/grafana/dashboards/ledger-overview.json` has four new log panels that I added for this test. That change was made directly and not reviewed.
- Nothing else in the repository changed.

## 10. Decision on the connection limit

Asked in the review of this report: what should happen with the connection limit (findings 1 and 2)?

Your decision: investigate and fix it now, before the next sub-project.

### 10.1 What was done

- Investigation: reproduced the hot-account phase alone. With the driver's default pool of 100, 5,673 connection attempts were refused in 30 seconds and 1,287 of the other callers' 1,989 requests failed. With the pool limited to 50, none were refused and none failed.
- The "Failed to connect to 127.0.0.1:5432" errors of phases 4 and 5 were Windows socket error 10048: the machine ran out of outgoing network ports, because every refused connection attempt uses up a port for a while. They were an effect of the refusals on one machine, not a separate fault.
- Fix: the API limits its connection pool to 50 unless the connection string sets `Maximum Pool Size` itself (`src/UmutLedger/Infrastructure/Persistence/LedgerConnectionPool.cs`). `docker-compose.yml` states PostgreSQL's `max_connections=100` explicitly. The README has the rule: instances x pool size must stay below `max_connections`.
- Verified: 317 tests pass at 100% coverage. The same 150-caller test against the API with its normal settings then completed 6,696 busy-account and 1,048 other transfers with no errors, and `psql` could still connect while the API held its 50 connections.
- Still open, by your decision: callers on other accounts are slowed while one account is busy (about 460 ms per request against 16 ms when calm). Your idea of a per-account line inside the API would address this. It is deferred, and these tests will be repeated once the application runs on Kubernetes with several instances.
