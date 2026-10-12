# The Idempotency Breach, Explained

Date: 2026-10-11
About: the Critical defect found in the review of Task 6 (`TransferService`), and its fix.

## 1. What idempotency is, and why a ledger needs it

A client sends "move 100 from Alice to Bob". The network drops the response. The client cannot tell whether the transfer happened, so it sends the same request again.

- Without idempotency, the second request moves another 100. Alice has paid twice.
- With idempotency, the second request is recognised as a repeat and gets the result of the first. Alice has paid once.

The client makes a repeat recognisable by sending the same **idempotency key** on both attempts, in the `Idempotency-Key` header. The promise the ledger makes is:

> For one key, the transfer happens at most once, and every request with that key and the same contents gets the same answer.

`CLAUDE.md` states it as a rule: "Transactions must be idempotent".

## 2. How the ledger keeps that promise

Each transfer row stores its key, and the database has a unique index on that column (`ux_transfers_idempotency_key`). The service's flow, as first planned, was:

1. Start a database transaction.
2. Look the key up in `transfers`. If a transfer with this key exists, return it (a "replay") and stop.
3. Lock both account rows with `SELECT ... FOR UPDATE`.
4. Check the accounts and the funds, then debit and credit.
5. Insert the transfer and its two entries, save the balances, commit.

For a retry that arrives **after** the first request has finished, this is correct: step 2 finds the transfer and replays it.

The breach was in what happens when the two requests arrive **at the same time**.

## 3. Two facts about PostgreSQL that the breach depends on

**Fact 1: a transaction cannot see another transaction's uncommitted rows.**
The ledger runs at PostgreSQL's default isolation level, read committed. Under it, each statement sees only data that was committed before that statement started. So while request A is still in progress, request B's lookup in step 2 does not see A's transfer row, even if A has already inserted it.

**Fact 2: `FOR UPDATE` makes the second transaction wait.**
When A has locked Alice's row, B's `SELECT ... FOR UPDATE` on the same row blocks. It waits until A commits or rolls back. When B is finally given the lock, the row it receives is the newest committed version, so B sees Alice's balance **after** A's transfer.

Put together: B can pass the key lookup without seeing A, then wait behind A, then wake up looking at a balance A has already changed.

## 4. The breach, step by step

Setup: Alice has 100. Bob has 0. Two identical requests, A and B, both say "move 100 from Alice to Bob" and both carry key `K`.

| Time | Request A | Request B | Alice's committed balance |
|---|---|---|---|
| 1 | Looks up `K`: not found | | 100 |
| 2 | | Looks up `K`: not found (A has committed nothing) | 100 |
| 3 | Locks Alice and Bob | | 100 |
| 4 | | Tries to lock Alice and Bob: **waits** | 100 |
| 5 | Checks funds: 100 is enough. Debits Alice, credits Bob | (waiting) | 100 |
| 6 | Inserts the transfer with key `K`, commits | (waiting) | **0** |
| 7 | Returns 201 with the transfer | Gets the locks. Reads Alice's balance: 0 | 0 |
| 8 | | Checks funds: 0 is not enough for 100 | 0 |
| 9 | | **Returns 422 `insufficient_funds`** | 0 |

The transfer happened exactly once, which is correct. The money is right. What is wrong is B's **answer**: B was told the transfer failed for lack of funds, when the transfer with its key had succeeded.

## 5. Why a wrong answer is a money problem

The answer is how the client learns what happened. Consider the normal reason for two identical requests: the client's first attempt (A) timed out on its side, so it retried (B).

1. The client never received A's 201.
2. It receives B's 422 `insufficient_funds`.
3. It concludes the payment did not happen.
4. It tops up Alice's account and sends the payment again, as a new attempt with a new key `K2`.
5. That transfer succeeds. Bob has now been paid 200 for a 100 payment.

The ledger did nothing wrong on any single request, and its books still balance. But the promise from section 1, "every request with that key gets the same answer", was broken, and that is what led the client to pay twice.

There was also a second, lesser symptom from the same cause. If B was a *different* request reusing key `K` on the same accounts, it should have been rejected with `idempotency_key_reused`. Instead it too got `insufficient_funds`, which hides the real mistake from the caller.

## 6. Why it stayed hidden when the account had enough money

Change one number: Alice starts with 300.

| Time | Request B, after A committed |
|---|---|
| 7 | Gets the locks. Reads Alice's balance: 200 |
| 8 | Checks funds: 200 is enough. Debits Alice, credits Bob (in memory) |
| 9 | Inserts a transfer with key `K`: the unique index rejects it |
| 10 | B's transaction rolls back, undoing its debit and credit |
| 11 | B reads the committed transfer with key `K` and returns it as a replay |

Here the unique index catches the duplicate and the outcome is right. The safety net sat at the **insert**, which is the last step. The funds check comes **before** the insert. So when the first request had drained the account, B failed at the funds check and never reached the safety net.

This is why the defect is easy to miss: it only appears when the winner leaves too little money for a second run, which is exactly the case of someone paying out their whole balance.

It is also why the forced test in my original plan would not have exposed it. That test funded the account with twice the transfer amount, precisely so that the loser's second execution would have enough money to reach the insert. I treated that as a detail of the test setup. It was the defect.

## 7. The fix

Look the key up a second time, **after** the locks are held and **before** the accounts are used.

1. Start a database transaction.
2. Look the key up. If found, replay and stop.
3. Lock both account rows with `SELECT ... FOR UPDATE`.
4. **Look the key up again. If found, replay and stop.**
5. Check the accounts and the funds, then debit and credit.
6. Insert the transfer and its two entries, save the balances, commit.

Why this works, using the two facts from section 3:

- B can only get past step 3 after A has committed or rolled back (Fact 2).
- Step 4 is a new statement, so it sees everything committed before it started (Fact 1). That now includes A's transfer.
- So B finds A's transfer in step 4 and replays it. B never looks at the balance at all.

The same timeline with the fix:

| Time | Request A | Request B | Alice's committed balance |
|---|---|---|---|
| 1 | Looks up `K`: not found | | 100 |
| 2 | | Looks up `K`: not found | 100 |
| 3 | Locks Alice and Bob | | 100 |
| 4 | Looks up `K` again: not found | Tries to lock: **waits** | 100 |
| 5 | Debits Alice, credits Bob, inserts, commits | (waiting) | **0** |
| 6 | Returns 201 with the transfer | Gets the locks | 0 |
| 7 | | Looks up `K` again: **found** | 0 |
| 8 | | **Returns 200 with A's transfer** | 0 |

Both requests now report the same transfer. The client that received only B's answer knows the payment went through.

If A had rolled back instead of committing (for example, A itself lacked funds), B's second lookup finds nothing, and B correctly goes on to become the one real attempt.

### The code

Before:

```csharp
var existing = await TransfersWithEntries().SingleOrDefaultAsync(t => t.IdempotencyKey == instruction.Key, ct);
if (existing is not null)
{
    return Replay(existing, instruction);
}

var lockedAccounts = await LockAccountsAsync(instruction, ct);
var debitAccount = Find(lockedAccounts, instruction.DebitAccountId);
```

After (`src/UmutLedger/Application/Transfers/TransferService.cs`, lines 65 to 80):

```csharp
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
```

`FindReplayAsync` is one small method holding the lookup and the replay rule, so the rule is written once and used at both points.

### Why the first lookup stays

The first lookup is no longer needed for correctness. It stays because a plain retry, arriving after the original has finished, is by far the common case, and the first lookup answers it without taking any locks.

### Why the unique index stays

The second lookup only helps when the two requests wait on each other's locks, which requires them to share an account. Two requests can share a key and share no account: for example `K` on "Alice pays Bob" and `K` on "Carol pays Dave". Neither waits for the other, so both pass both lookups. Only the unique index can stop the second. Its insert fails, its transaction rolls back, and it is rejected with `idempotency_key_reused`, because the requests differ.

## 8. Every case, and what happens now

| Two requests with the same key | What they share | Who stops the second | Second request gets |
|---|---|---|---|
| Identical, one after the other | Everything | First lookup | 200, the original transfer |
| Identical, at the same time | Both accounts | Second lookup, after the locks | 200, the original transfer |
| Different, at the same time | One or both accounts | Second lookup, after the locks | 422 `idempotency_key_reused` |
| Different, at the same time | No account | The unique index | 422 `idempotency_key_reused` |
| Any, where the first one failed and wrote nothing | — | Nobody | It runs as a fresh attempt |

The scoped re-review walked each of these under read committed, including deposits and withdrawals (which share a per-currency world account), and found none left where a request gets an answer that disagrees with what was committed.

## 9. How the fix was proved

A race like this cannot be tested reliably by firing requests in parallel and hoping they interleave the right way. The test forces the interleaving:

1. The test opens its own database transaction and performs the winner's transfer in it, without committing. The winner now holds the account locks.
2. The test starts the real service call (the loser) on another connection. It passes the first lookup and blocks on the locks.
3. The test asks PostgreSQL, through `pg_blocking_pids`, whether a connection is now blocked by the winner's connection. It polls until the answer is yes. This is what makes the test deterministic: the loser is known to be past the first lookup and waiting.
4. The test commits the winner.
5. The loser wakes up. The test asserts what it returns.

Test: `DuplicateKeyRace_LoserBlockedBehindAWinnerThatDrainsTheAccount_ReplaysTheWinner` in `tests/UmutLedger.Tests/Application/TransferConcurrencyTests.cs`. Alice is funded with exactly the transfer amount, so the winner drains her.

- Before the fix: the test failed with `InsufficientFundsException`.
- After the fix: it passes. The loser's outcome is a replay with the winner's transfer id, Alice's balance is 0 and Bob's is 100.
- It passed five repeat runs at the time, and three more after the final fix wave.

A second forced test, `DuplicateKeyRace_SameKeyOnUnrelatedAccounts_LoserIsStoppedByTheUniqueConstraint`, covers the unique-index path from section 7.

## 10. Where the defect came from, and how it was caught

- **Origin:** my spec and my plan. The spec's rule for "two identical requests at the same instant" described only the unique-index path. The implementer built the plan's code exactly, and all 28 of that task's tests passed.
- **Why the tests missed it:** the sequential replay test pins the retry-after-completion case, where the first lookup works. No test in the plan had a winner that drained the account while a loser was waiting.
- **How it was caught:** the Task 6 reviewer read the flow against PostgreSQL's read-committed behaviour and described the exact interleaving in section 4. It did not run a test; the implementer's forced test then confirmed it.
- **What it cost:** one fix round on Task 6, before Tasks 7 to 10 were built on it.
- **What changed besides the code:** spec section 7 (step 3 and the idempotency rules) and plan Tasks 6 and 7 were amended to describe the corrected flow.

## 11. Terms used

| Term | Meaning |
|---|---|
| Idempotency key | A value the client chooses and sends with a request, the same on every retry of that request |
| Replay | Returning the already-committed transfer for a key, instead of executing again |
| Read committed | PostgreSQL's default isolation level: each statement sees only data committed before it started |
| `FOR UPDATE` | A row lock taken while reading; a second transaction asking for the same lock waits |
| Winner / loser | Of two racing requests, the one that commits first and the one that does not |
| Unique index | A database rule that no two rows may have the same value in a column |
