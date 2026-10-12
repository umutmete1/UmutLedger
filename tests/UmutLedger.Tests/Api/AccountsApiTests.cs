using System.Net;
using UmutLedger.Api.Models;
using UmutLedger.Domain.Enums;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class AccountsApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private readonly HttpClient _client = api.Client;

    [Fact]
    public async Task CreateAccount_ValidRequest_Returns201WithTheAccountAndItsLocation()
    {
        var response = await _client.PostLedgerAsync("/accounts", new { name = "Alice", currency = "EUR" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = await response.ReadAsync<AccountResponse>();
        Assert.Equal("Alice", account.Name);
        Assert.Equal(Currency.EUR, account.Currency);
        Assert.Equal(AccountType.Customer, account.Type);
        Assert.Equal(0, account.Balance);
        Assert.EndsWith($"/accounts/{account.Id}", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("""{"name":"   ","currency":"USD"}""")]
    [InlineData("""{"name":"Alice","currency":"JPY"}""")]
    [InlineData("""{"name":"Alice","currency":1}""")]
    [InlineData("""{"name":"Alice"}""")]
    [InlineData("""{"currency":"USD"}""")]
    [InlineData("""{"name":null,"currency":"USD"}""")]
    [InlineData("""{"name":"Alice","currency":""")]
    [InlineData("")]
    public async Task CreateAccount_InvalidBody_Returns400ValidationFailed(string json)
    {
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/accounts", content);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task CreateAccount_NameOverTheLimit_Returns400ValidationFailed()
    {
        var response = await _client.PostLedgerAsync("/accounts", new { name = new string('a', 101), currency = "USD" });

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task CreateAccount_WrongContentType_Returns415UnsupportedMediaType()
    {
        var content = new StringContent("name=Alice", System.Text.Encoding.UTF8, "text/plain");

        var response = await _client.PostAsync("/accounts", content);

        await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    [Fact]
    public async Task GetAccount_ExistingAccount_Returns200WithItsBalance()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(750);

        var response = await _client.GetAsync($"/accounts/{accountId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var account = await response.ReadAsync<AccountResponse>();
        Assert.Equal(accountId, account.Id);
        Assert.Equal(750, account.Balance);
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task GetAccount_SystemAccount_IsReadable()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.GBP);

        var response = await _client.GetAsync($"/accounts/{worldAccountId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AccountType.System, (await response.ReadAsync<AccountResponse>()).Type);
    }

    [Fact]
    public async Task GetAccount_UnknownAccount_Returns404AccountNotFound()
    {
        var response = await _client.GetAsync($"/accounts/{Guid.CreateVersion7()}");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
    }

    [Fact]
    public async Task GetAccount_IdIsNotAGuid_Returns400ValidationFailed()
    {
        var response = await _client.GetAsync("/accounts/not-a-guid");

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task GetEntries_SeveralEntries_PagesNewestFirstWithACursor()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime.AddSeconds(1));
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(200), TestLedger.BaseTime.AddSeconds(2));
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(300), TestLedger.BaseTime.AddSeconds(3));

        var firstPage = await (await _client.GetAsync($"/accounts/{accountId}/entries?limit=2")).ReadAsync<EntryPageResponse>();
        var secondPage = await (await _client.GetAsync($"/accounts/{accountId}/entries?limit=2&cursor={firstPage.NextCursor}")).ReadAsync<EntryPageResponse>();

        Assert.Equal([300, 200], firstPage.Items.Select(entry => entry.Amount));
        Assert.NotNull(firstPage.NextCursor);
        Assert.Equal([100], secondPage.Items.Select(entry => entry.Amount));
        Assert.Null(secondPage.NextCursor);
        Assert.All(firstPage.Items, entry => Assert.Equal(accountId, entry.AccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task GetEntries_NoLimit_Returns200()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        var response = await _client.GetAsync($"/accounts/{accountId}/entries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await response.ReadAsync<EntryPageResponse>()).Items);
        await LedgerInvariants.AssertHoldAsync();
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("limit=abc")]
    [InlineData("cursor=not-a-guid")]
    public async Task GetEntries_InvalidPaging_Returns400ValidationFailed(string query)
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.GetAsync($"/accounts/{accountId}/entries?{query}");

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task GetEntries_UnknownAccount_Returns404AccountNotFound()
    {
        var response = await _client.GetAsync($"/accounts/{Guid.CreateVersion7()}/entries");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
    }
}
