using Microsoft.AspNetCore.Mvc;
using UmutLedger.Api.Models;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Api.Controllers;

[ApiController]
public sealed class TransfersController(ITransferService transfers) : ControllerBase
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    [HttpPost("transfers")]
    public async Task<ActionResult<TransferResponse>> Create(
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CreateTransferRequest request,
        CancellationToken cancellationToken) =>
        ToResult(await transfers.TransferAsync(
            new IdempotencyKey(idempotencyKey),
            request.DebitAccountId,
            request.CreditAccountId,
            new Money(request.Amount, request.Currency),
            cancellationToken));

    [HttpPost("accounts/{id}/deposits")]
    public async Task<ActionResult<TransferResponse>> Deposit(
        Guid id,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        MoneyRequest request,
        CancellationToken cancellationToken) =>
        ToResult(await transfers.DepositAsync(
            new IdempotencyKey(idempotencyKey), id, new Money(request.Amount, request.Currency), cancellationToken));

    [HttpPost("accounts/{id}/withdrawals")]
    public async Task<ActionResult<TransferResponse>> Withdraw(
        Guid id,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        MoneyRequest request,
        CancellationToken cancellationToken) =>
        ToResult(await transfers.WithdrawAsync(
            new IdempotencyKey(idempotencyKey), id, new Money(request.Amount, request.Currency), cancellationToken));

    [HttpGet("transfers/{id}")]
    public async Task<TransferResponse> Get(Guid id, CancellationToken cancellationToken) =>
        TransferResponse.From(await transfers.GetAsync(id, cancellationToken));

    private ActionResult<TransferResponse> ToResult(TransferOutcome outcome)
    {
        var response = TransferResponse.From(outcome.Transfer);
        return outcome.WasReplayed
            ? Ok(response)
            : CreatedAtAction(nameof(Get), new { id = response.Id }, response);
    }
}
