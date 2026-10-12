using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Domain.Entities;

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
        if (idempotencyKey is null)
        {
            throw new IdempotencyKeyRequiredException();
        }

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
        Require.NotNull(debitAccount, "Debit account");
        Require.NotNull(creditAccount, "Credit account");
        Require.NotNull(amount, "Amount");
        var transfer = new Transfer(id, idempotencyKey, debitAccount.Id, creditAccount.Id, amount.Amount, amount.Currency, createdAt);
        creditAccount.EnsureCanCredit(amount);
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
