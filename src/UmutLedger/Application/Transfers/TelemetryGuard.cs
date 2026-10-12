namespace UmutLedger.Application.Transfers;

internal static class TelemetryGuard
{
    // Telemetry is never allowed to change a money result, so whatever it throws stays here.
    public static void Contain(Action telemetry, Action<Exception> reportFailure)
    {
        try
        {
            telemetry();
        }
        catch (Exception exception)
        {
            // A failing report has nowhere left to be reported, and swallowing it is the point of this guard.
            Contain(() => reportFailure(exception), _ => { });
        }
    }
}
