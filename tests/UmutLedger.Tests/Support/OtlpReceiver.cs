using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace UmutLedger.Tests.Support;

// Accepts OTLP over HTTP on a free local port and remembers which signals arrived.
public sealed class OtlpReceiver : IAsyncDisposable
{
    private const string AnyFreeLocalPort = "http://127.0.0.1:0";

    private readonly ConcurrentQueue<string> _signals = new();
    private readonly WebApplication _app;

    private OtlpReceiver()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(AnyFreeLocalPort);
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapPost("/v1/{signal}", (string signal) =>
        {
            _signals.Enqueue(signal);
            return Results.Ok();
        });
    }

    public string Url => _app.Urls.Single();

    public IReadOnlyList<string> Signals => _signals.Distinct().Order().ToList();

    public static async Task<OtlpReceiver> StartAsync()
    {
        var receiver = new OtlpReceiver();
        await receiver._app.StartAsync();
        return receiver;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
