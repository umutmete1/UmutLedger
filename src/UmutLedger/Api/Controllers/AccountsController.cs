using Microsoft.AspNetCore.Mvc;
using UmutLedger.Api.Models;
using UmutLedger.Application.Accounts;

namespace UmutLedger.Api.Controllers;

[ApiController]
[Route("accounts")]
public sealed class AccountsController(IAccountService accounts) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<AccountResponse>> Create(CreateAccountRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.CreateAsync(request.Name, request.Currency, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = account.Id }, AccountResponse.From(account));
    }

    [HttpGet("{id}")]
    public async Task<AccountResponse> Get(Guid id, CancellationToken cancellationToken) =>
        AccountResponse.From(await accounts.GetAsync(id, cancellationToken));

    [HttpGet("{id}/entries")]
    public async Task<EntryPageResponse> GetEntries(Guid id, [FromQuery] int? limit, [FromQuery] Guid? cursor, CancellationToken cancellationToken) =>
        EntryPageResponse.From(await accounts.GetEntriesAsync(id, new PageRequest(limit, cursor), cancellationToken));
}
