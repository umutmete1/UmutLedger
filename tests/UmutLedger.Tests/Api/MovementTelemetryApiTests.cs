using System.Net;
using Microsoft.Extensions.DependencyInjection;
using UmutLedger.Application.Transfers;
using UmutLedger.Infrastructure.Telemetry;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class MovementTelemetryApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    [Fact]
    public void TransferService_IsResolvedAsTheInstrumentedDecorator()
    {
        using var scope = api.Services.CreateScope();

        Assert.IsType<InstrumentedTransferService>(scope.ServiceProvider.GetRequiredService<ITransferService>());
    }

    [Fact]
    public void LedgerMetrics_IsResolvedAsTheFailSafeDecorator()
    {
        Assert.IsType<FailSafeLedgerMetrics>(api.Services.GetRequiredService<ILedgerMetrics>());
    }

    [Fact]
    public async Task Deposit_TagsItsRequestSpan()
    {
        using var spans = new RequestSpanRecorder();
        var accountId = await TestLedger.CreateCustomerAsync();
        var traceId = TraceContext.NewTraceId();

        var response = await api.Client.PostLedgerAsync(
            $"/accounts/{accountId}/deposits",
            new { amount = 500, currency = "USD" },
            Guid.NewGuid().ToString(),
            TraceContext.ParentHeaderFor(traceId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var span = spans.SpanOf(traceId);
        Assert.Equal("deposit", span.GetTagItem(InstrumentedTransferService.KindAttribute));
        Assert.Equal(MovementOutcomes.Created, span.GetTagItem(InstrumentedTransferService.OutcomeAttribute));
        Assert.NotNull(span.GetTagItem(InstrumentedTransferService.TransferIdAttribute));
    }
}
