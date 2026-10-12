using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class ApiDocumentationTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private const string ApiReferencePage = "/scalar";
    private const string OpenApiDocument = "/openapi/v1.json";

    [Fact]
    public async Task ApiReference_InDevelopment_IsServed()
    {
        var response = await api.Client.GetAsync(ApiReferencePage);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("scalar", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApiReference_InDevelopment_ShowsTheLedgerTitle()
    {
        var response = await api.Client.GetAsync(ApiReferencePage);

        Assert.Contains("UmutLedger API", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/accounts")]
    [InlineData("/accounts/{id}")]
    [InlineData("/accounts/{id}/entries")]
    [InlineData("/accounts/{id}/deposits")]
    [InlineData("/accounts/{id}/withdrawals")]
    [InlineData("/transfers")]
    [InlineData("/transfers/{id}")]
    public async Task OpenApiDocument_InDevelopment_DescribesEveryLedgerPath(string path)
    {
        var response = await api.Client.GetAsync(OpenApiDocument);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.ReadAsync<JsonElement>();
        Assert.True(document.GetProperty("paths").TryGetProperty(path, out _), $"The OpenAPI document does not describe {path}.");
    }

    [Fact]
    public async Task OpenApiDocument_InDevelopment_DescribesTheIdempotencyKeyHeader()
    {
        var response = await api.Client.GetAsync(OpenApiDocument);

        Assert.Contains(ApiExtensions.IdempotencyKeyHeader, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiReference_OutsideDevelopment_IsNotServed()
    {
        await using var factory = LedgerApi.CreateFactory(await TestDatabase.GetConnectionStringAsync(), Environments.Production);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(ApiReferencePage);

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
    }
}
