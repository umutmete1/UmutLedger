using System.Net;
using UmutLedger.Api.Models;
using UmutLedger.Domain.Enums;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class TransfersApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    private readonly HttpClient _client = api.Client;

    private static string NewKey() => Guid.NewGuid().ToString();

    private static object Usd(long amount) => new { amount, currency = "USD" };

    private static object TransferBody(Guid debitAccountId, Guid creditAccountId, long amount, string currency = "USD") =>
        new { debitAccountId, creditAccountId, amount, currency };

    [Fact]
    public async Task Deposit_ValidRequest_Returns201WithTheTransferAndItsLocation()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transfer = await response.ReadAsync<TransferResponse>();
        Assert.Equal(key, transfer.IdempotencyKey);
        Assert.Equal(accountId, transfer.CreditAccountId);
        Assert.Equal(500, transfer.Amount);
        Assert.Equal(Currency.USD, transfer.Currency);
        Assert.Equal(2, transfer.Entries.Count);
        Assert.Equal(0, transfer.Entries.Sum(entry => entry.Amount));
        Assert.EndsWith($"/transfers/{transfer.Id}", response.Headers.Location!.ToString());
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Deposit_SameKeyAndSameRequest_Returns200WithTheOriginalTransfer()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        var original = await (await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), key)).ReadAsync<TransferResponse>();

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original.Id, (await response.ReadAsync<TransferResponse>()).Id);
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Deposit_WithoutIdempotencyKey_Returns400KeyRequired()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500));

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        Assert.Equal(0, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Deposit_KeyOverTheLimit_Returns400ValidationFailed()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), new string('k', 256));

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        await LedgerInvariants.AssertHoldAsync();
    }

    private const long FundedBalance = 500;

    public static TheoryData<string, string> InvalidMoneyBodiesForBothRoutes()
    {
        string[] bodies =
        [
            """{"amount":0,"currency":"USD"}""",
            """{"amount":-5,"currency":"USD"}""",
            """{"amount":10.5,"currency":"USD"}""",
            """{"amount":100,"currency":"JPY"}""",
            """{"currency":"USD"}""",
            """{"amount":100}"""
        ];
        var data = new TheoryData<string, string>();
        foreach (var route in new[] { "deposits", "withdrawals" })
        {
            foreach (var body in bodies)
            {
                data.Add(route, body);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidMoneyBodiesForBothRoutes))]
    public async Task AccountMoneyRoute_InvalidBody_Returns400ValidationFailed(string route, string json)
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(FundedBalance);

        var response = await PostRawAsync($"/accounts/{accountId}/{route}", json, NewKey());

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(FundedBalance, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    private async Task<HttpResponseMessage> PostRawAsync(string url, string json, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(ApiExtensions.IdempotencyKeyHeader, key);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task Withdrawal_WithoutIdempotencyKey_Returns400KeyRequired()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(FundedBalance);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(100));

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        Assert.Equal(FundedBalance, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Withdrawal_UnknownAccount_Returns404AccountNotFound()
    {
        var response = await _client.PostLedgerAsync($"/accounts/{Guid.CreateVersion7()}/withdrawals", Usd(100), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Withdrawal_CurrencyDiffersFromTheAccount_Returns422CurrencyMismatch()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(FundedBalance, Currency.EUR);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(100), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "currency_mismatch");
        Assert.Equal(FundedBalance, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Withdrawal_FromASystemAccount_Returns422SystemAccountNotAllowed()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        var response = await _client.PostLedgerAsync($"/accounts/{worldAccountId}/withdrawals", Usd(100), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "system_account_not_allowed");
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Withdrawal_SameKeyAndSameRequest_Returns200WithTheOriginalTransfer()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(FundedBalance);
        var key = NewKey();
        var original = await (await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(200), key)).ReadAsync<TransferResponse>();

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(200), key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original.Id, (await response.ReadAsync<TransferResponse>()).Id);
        Assert.Equal(FundedBalance - 200, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Withdrawal_SameKeyButDifferentAmount_Returns422KeyReused()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(FundedBalance);
        var key = NewKey();
        await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(200), key);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(201), key);

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(FundedBalance - 200, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Deposit_SameKeyButDifferentAmount_Returns422KeyReused()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), key);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(501), key);

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    public static TheoryData<string> InvalidTransferDefects() =>
        [
            "ZeroAmount",
            "NegativeAmount",
            "UnsupportedCurrency",
            "MissingDebitAccountId",
            "MissingCreditAccountId",
            "DebitAccountIdNotAGuid"
        ];

    private static string TransferJsonWithDefect(string defect, Guid debitAccountId, Guid creditAccountId) => defect switch
    {
        "ZeroAmount" => $$"""{"debitAccountId":"{{debitAccountId}}","creditAccountId":"{{creditAccountId}}","amount":0,"currency":"USD"}""",
        "NegativeAmount" => $$"""{"debitAccountId":"{{debitAccountId}}","creditAccountId":"{{creditAccountId}}","amount":-5,"currency":"USD"}""",
        "UnsupportedCurrency" => $$"""{"debitAccountId":"{{debitAccountId}}","creditAccountId":"{{creditAccountId}}","amount":10,"currency":"JPY"}""",
        "MissingDebitAccountId" => $$"""{"creditAccountId":"{{creditAccountId}}","amount":10,"currency":"USD"}""",
        "MissingCreditAccountId" => $$"""{"debitAccountId":"{{debitAccountId}}","amount":10,"currency":"USD"}""",
        "DebitAccountIdNotAGuid" => $$"""{"debitAccountId":"not-a-guid","creditAccountId":"{{creditAccountId}}","amount":10,"currency":"USD"}""",
        _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, null)
    };

    [Theory]
    [MemberData(nameof(InvalidTransferDefects))]
    public async Task Transfer_InvalidBody_Returns400ValidationFailed(string defect)
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(FundedBalance);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await PostRawAsync("/transfers", TransferJsonWithDefect(defect, debitAccountId, creditAccountId), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(FundedBalance, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(0, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_KeyOverTheLimit_Returns400ValidationFailed()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(FundedBalance);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 10), new string('k', 256));

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(FundedBalance, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_CreditSideIsASystemAccount_Returns422SystemAccountNotAllowed()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(FundedBalance);
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, worldAccountId, 10), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "system_account_not_allowed");
        Assert.Equal(FundedBalance, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_UnknownDebitAccount_Returns404AccountNotFound()
    {
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(Guid.CreateVersion7(), creditAccountId, 10), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
        Assert.Equal(0, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Deposit_UnknownAccount_Returns404AccountNotFound()
    {
        var response = await _client.PostLedgerAsync($"/accounts/{Guid.CreateVersion7()}/deposits", Usd(500), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Deposit_CurrencyDiffersFromTheAccount_Returns422CurrencyMismatch()
    {
        var accountId = await TestLedger.CreateCustomerAsync(Currency.EUR);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/deposits", Usd(500), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "currency_mismatch");
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Deposit_IntoASystemAccount_Returns422SystemAccountNotAllowed()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        var response = await _client.PostLedgerAsync($"/accounts/{worldAccountId}/deposits", Usd(500), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "system_account_not_allowed");
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Withdrawal_WithEnoughFunds_Returns201AndReducesTheBalance()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(500);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(200), NewKey());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(accountId, (await response.ReadAsync<TransferResponse>()).DebitAccountId);
        Assert.Equal(300, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Withdrawal_MoreThanTheBalance_Returns422InsufficientFunds()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        var response = await _client.PostLedgerAsync($"/accounts/{accountId}/withdrawals", Usd(101), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "insufficient_funds");
        Assert.Equal(100, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_ValidRequest_Returns201AndMovesTheMoney()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 400), NewKey());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transfer = await response.ReadAsync<TransferResponse>();
        Assert.Equal(debitAccountId, transfer.DebitAccountId);
        Assert.Equal(creditAccountId, transfer.CreditAccountId);
        Assert.EndsWith($"/transfers/{transfer.Id}", response.Headers.Location!.ToString());
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(400, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_SameKeyAndSameRequest_Returns200WithoutMovingMoneyAgain()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        var body = TransferBody(debitAccountId, creditAccountId, 400);
        var original = await (await _client.PostLedgerAsync("/transfers", body, key)).ReadAsync<TransferResponse>();

        var response = await _client.PostLedgerAsync("/transfers", body, key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original.Id, (await response.ReadAsync<TransferResponse>()).Id);
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_SameKeyButDifferentAmount_Returns422KeyReused()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 400), key);

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 401), key);

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_KeyAlreadyUsedByADeposit_Returns422KeyReused()
    {
        var debitAccountId = await TestLedger.CreateCustomerAsync();
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        await _client.PostLedgerAsync($"/accounts/{debitAccountId}/deposits", Usd(500), key);

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 500), key);

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(500, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_SameAccountOnBothSides_Returns400SameAccountTransfer()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(accountId, accountId, 10), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "same_account_transfer");
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_WithoutIdempotencyKey_Returns400KeyRequired()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 10));

        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_UnknownAccount_Returns404AccountNotFound()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, Guid.CreateVersion7(), 10), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "account_not_found");
        Assert.Equal(100, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_CurrencyDiffersFromTheAccounts_Returns422CurrencyMismatch()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 10, "EUR"), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "currency_mismatch");
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_Returns422InsufficientFunds()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(debitAccountId, creditAccountId, 101), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "insufficient_funds");
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_NamesASystemAccount_Returns422SystemAccountNotAllowed()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var response = await _client.PostLedgerAsync("/transfers", TransferBody(worldAccountId, creditAccountId, 10), NewKey());

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "system_account_not_allowed");
        Assert.Equal(0, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task GetTransfer_ExistingTransfer_Returns200WithItsEntries()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var created = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);

        var response = await _client.GetAsync($"/transfers/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var transfer = await response.ReadAsync<TransferResponse>();
        Assert.Equal(created.Id, transfer.Id);
        Assert.Equal(2, transfer.Entries.Count);
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task Transfer_Entries_AreAlwaysDebitFirst()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = NewKey();
        var body = TransferBody(debitAccountId, creditAccountId, 400);

        var created = await (await _client.PostLedgerAsync("/transfers", body, key)).ReadAsync<TransferResponse>();
        var fetched = await (await _client.GetAsync($"/transfers/{created.Id}")).ReadAsync<TransferResponse>();
        var replayed = await (await _client.PostLedgerAsync("/transfers", body, key)).ReadAsync<TransferResponse>();

        Assert.All([created, fetched, replayed], transfer =>
        {
            Assert.True(transfer.Entries[0].Amount < 0);
            Assert.True(transfer.Entries[1].Amount > 0);
        });
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task GetTransfer_UnknownTransfer_Returns404TransferNotFound()
    {
        var response = await _client.GetAsync($"/transfers/{Guid.CreateVersion7()}");

        await response.AssertProblemAsync(HttpStatusCode.NotFound, "transfer_not_found");
    }
}
