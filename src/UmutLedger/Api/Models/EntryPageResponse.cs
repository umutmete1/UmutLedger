using UmutLedger.Application.Accounts;

namespace UmutLedger.Api.Models;

public sealed record EntryPageResponse(IReadOnlyList<EntryResponse> Items, Guid? NextCursor)
{
    public static EntryPageResponse From(EntryPage page) =>
        new(page.Items.Select(EntryResponse.From).ToList(), page.NextCursor);
}
