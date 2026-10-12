using System.Net;
using Microsoft.Extensions.Hosting;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class ErrorHandlingApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=ledger;Username=ledger;Password=ledger;Timeout=2";

    [Fact]
    public async Task UnexpectedException_Returns500WithoutLeakingDetails()
    {
        var response = await api.Client.GetAsync("/test/throw");

        var body = await response.Content.ReadAsStringAsync();
        await response.AssertProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain(ThrowingController.SecretMessage, body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task UnknownRoute_Returns404NotFound()
    {
        var response = await api.Client.GetAsync("/no-such-route");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task WrongMethod_Returns405MethodNotAllowed()
    {
        var response = await api.Client.DeleteAsync("/accounts");

        await response.AssertProblemAsync(HttpStatusCode.MethodNotAllowed, "method_not_allowed");
    }

    [Fact]
    public async Task DatabaseUnreachable_Returns503ServiceUnavailable()
    {
        await using var factory = LedgerApi.CreateFactory(UnreachableDatabase, Environments.Production);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/accounts/{Guid.CreateVersion7()}");

        await response.AssertProblemAsync(HttpStatusCode.ServiceUnavailable, "service_unavailable");
    }
}
