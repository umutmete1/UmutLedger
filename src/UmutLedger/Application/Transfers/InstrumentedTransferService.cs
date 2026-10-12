using System.Diagnostics;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Application.Transfers;

public sealed class InstrumentedTransferService(
    ITransferService inner, ILedgerMetrics metrics, ILogger<InstrumentedTransferService> logger) : ITransferService
{
    public const string KindAttribute = "ledger.movement.kind";
    public const string OutcomeAttribute = "ledger.movement.outcome";
    public const string TransferIdAttribute = "ledger.transfer.id";

    private const string AccountIdSeparator = ", ";

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
            Contain(nameof(ILedgerMetrics.RecordAmount), () => metrics.RecordAmount(movement.Kind, movement.Amount));
        }

        Report(movement, MovementOutcomes.Of(outcome), outcome.Transfer.Id);
        return outcome;
    }

    private void Report(Movement movement, string outcome, Guid? transferId)
    {
        Contain(
            nameof(ILedgerMetrics.RecordMovement),
            () => metrics.RecordMovement(movement.Kind, movement.Amount.Currency, outcome));
        Contain(
            nameof(Report),
            () =>
            {
                TagRequestSpan(movement.Kind, outcome, transferId);
                LogFinished(movement, outcome, transferId);
            });
    }

    private void Contain(string operation, Action telemetry) =>
        TelemetryGuard.Contain(telemetry, exception => logger.TelemetryFailed(exception, operation));

    private void LogFinished(Movement movement, string outcome, Guid? transferId) =>
        logger.MovementFinished(
            movement.Kind.ToLabel(),
            outcome,
            movement.Amount.Amount,
            movement.Amount.Currency,
            string.Join(AccountIdSeparator, movement.AccountIds),
            transferId);

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
