using UmutLedger.Domain.Enums;

namespace UmutLedger.Application.Transfers;

internal static partial class MovementLog
{
    private const int MovementFinishedEventId = 1;
    private const int TelemetryFailedEventId = 2;

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
        string accountIds,
        Guid? transferId);

    [LoggerMessage(
        EventId = TelemetryFailedEventId,
        Level = LogLevel.Warning,
        Message = "Telemetry failed during {Operation} and was ignored")]
    public static partial void TelemetryFailed(this ILogger logger, Exception exception, string operation);
}
