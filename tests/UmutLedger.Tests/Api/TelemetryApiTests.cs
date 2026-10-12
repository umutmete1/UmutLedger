using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using UmutLedger.Api.Telemetry;
using UmutLedger.Application.Transfers;
using UmutLedger.Infrastructure.Telemetry;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class TelemetryApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private const string UnreachableCollector = "http://127.0.0.1:1";
    private const string MalformedTraceParent = "not-a-trace";
    private const int FlushTimeoutMilliseconds = 10_000;

    private static HttpRequestMessage GetUnknownAccount(string traceParent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/accounts/{Guid.CreateVersion7()}");
        request.Headers.Add(TraceContext.HeaderName, traceParent);
        return request;
    }

    private static async Task<HttpResponseMessage> DepositAsync(HttpClient client)
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        return await client.PostLedgerAsync(
            $"/accounts/{accountId}/deposits", new { amount = 500, currency = "USD" }, Guid.NewGuid().ToString());
    }

    [Fact]
    public async Task ErrorResponse_TraceIdIsTheTraceIdOfTheRequest()
    {
        var traceId = TraceContext.NewTraceId();

        var response = await api.Client.SendAsync(GetUnknownAccount(TraceContext.ParentHeaderFor(traceId)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.ReadAsync<JsonElement>();
        Assert.Equal(traceId, problem.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task ErrorResponse_WithAMalformedTraceParent_StillCarriesATraceId()
    {
        var response = await api.Client.SendAsync(GetUnknownAccount(MalformedTraceParent));

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
    }

    [Fact]
    public async Task Deposit_IsCountedByTheApplicationsOwnMeter()
    {
        using var movements = new MetricCollector<long>(
            api.Services.GetRequiredService<IMeterFactory>(), LedgerMetrics.MeterName, LedgerMetrics.MovementsCounter);

        var response = await DepositAsync(api.Client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(movements.GetMeasurementSnapshot(), measurement =>
            Equals(measurement.Tags[LedgerMetrics.KindLabel], "deposit") &&
            Equals(measurement.Tags[LedgerMetrics.OutcomeLabel], MovementOutcomes.Created));
    }

    [Fact]
    public void TestHost_DoesNotExportByDefault() =>
        Assert.False(LedgerTelemetry.IsExportConfigured(api.Services.GetRequiredService<IConfiguration>()));

    [Fact]
    public async Task ConfiguredEndpoint_ReceivesTracesMetricsAndLogs()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var factory = LedgerApi.CreateFactory(
            await TestDatabase.GetConnectionStringAsync(), Environments.Development, receiver.Url);
        using var client = factory.CreateClient();

        var response = await DepositAsync(client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush(FlushTimeoutMilliseconds));
        Assert.True(factory.Services.GetRequiredService<MeterProvider>().ForceFlush(FlushTimeoutMilliseconds));
        Assert.True(factory.Services.GetRequiredService<LoggerProvider>().ForceFlush(FlushTimeoutMilliseconds));
        Assert.Equal(new[] { "logs", "metrics", "traces" }, receiver.Signals);
    }

    [Fact]
    public async Task UnreachableCollector_DoesNotFailARequest()
    {
        await using var factory = LedgerApi.CreateFactory(
            await TestDatabase.GetConnectionStringAsync(), Environments.Development, UnreachableCollector);
        using var client = factory.CreateClient();

        var response = await DepositAsync(client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
