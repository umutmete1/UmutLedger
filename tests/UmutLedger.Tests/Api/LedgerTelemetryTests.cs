using Microsoft.Extensions.Configuration;
using UmutLedger.Api.Telemetry;

namespace UmutLedger.Tests.Api;

public class LedgerTelemetryTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("http://localhost:4317", true)]
    public void IsExportConfigured_OnlyWhenAnEndpointIsSet(string? endpoint, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [LedgerTelemetry.OtlpEndpointKey] = endpoint })
            .Build();

        Assert.Equal(expected, LedgerTelemetry.IsExportConfigured(configuration));
    }
}
