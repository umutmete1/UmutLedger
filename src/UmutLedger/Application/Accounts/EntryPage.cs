using UmutLedger.Domain.Entities;

namespace UmutLedger.Application.Accounts;

public sealed class EntryPage
{
    private EntryPage(IReadOnlyList<Entry> items, Guid? nextCursor)
    {
        Items = items;
        NextCursor = nextCursor;
    }

    public IReadOnlyList<Entry> Items { get; }

    public Guid? NextCursor { get; }

    public static EntryPage From(IReadOnlyList<Entry> fetched, int limit)
    {
        var items = fetched.Take(limit).ToList();
        Guid? nextCursor = fetched.Count > limit ? items[^1].Id : null;
        return new EntryPage(items, nextCursor);
    }
}
