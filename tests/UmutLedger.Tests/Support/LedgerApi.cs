using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UmutLedger.Api.Telemetry;

namespace UmutLedger.Tests.Support;

public sealed class LedgerApi : IAsyncLifetime
{
    private const string NoExport = "";
    private const string OtlpProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string HttpProtobuf = "http/protobuf";

    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _factory.Services;

    public static WebApplicationFactory<Program> CreateFactory(
        string connectionString, string environment, string otlpEndpoint = NoExport) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // UseSetting is visible to Program.cs before the host is built; ConfigureAppConfiguration is not.
            // An empty endpoint also overrides appsettings.Development.json, so tests never export to a running Collector.
            builder.UseSetting(LedgerTelemetry.OtlpEndpointKey, otlpEndpoint);
            builder.UseSetting(OtlpProtocolKey, HttpProtobuf);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString }));
            builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(ThrowingController).Assembly));
        });

    public async Task InitializeAsync()
    {
        _factory = CreateFactory(await TestDatabase.GetConnectionStringAsync(), Environments.Development);
        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();
}
