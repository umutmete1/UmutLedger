namespace UmutLedger.Application.Transfers;

public static class MovementKindExtensions
{
    public static string ToLabel(this MovementKind kind) => kind.ToString().ToLowerInvariant();
}
