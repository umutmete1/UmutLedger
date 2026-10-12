using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using UmutLedger.Infrastructure.Telemetry;

namespace UmutLedger.Api.Telemetry;

public static class LedgerTelemetry
{
    public const string ServiceName = "umutledger";
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private const string NpgsqlMeterName = "Npgsql";
    private const string RuntimeMeterName = "System.Runtime";

    private static readonly string ServiceVersion = typeof(LedgerTelemetry).Assembly.GetName().Version!.ToString();

    public static IServiceCollection AddLedgerTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: ServiceVersion))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddMeter(LedgerMetrics.MeterName, NpgsqlMeterName, RuntimeMeterName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddNpgsql())
            .WithLogging(configureBuilder: null, configureOptions: options => options.IncludeFormattedMessage = true);

        if (IsExportConfigured(configuration))
        {
            telemetry.UseOtlpExporter();
        }

        return services;
    }

    public static bool IsExportConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]);
}
