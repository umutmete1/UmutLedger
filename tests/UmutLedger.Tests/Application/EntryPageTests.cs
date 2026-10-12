using UmutLedger.Application.Accounts;
using UmutLedger.Domain.Entities;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class EntryPageTests
{
    private static List<Entry> Entries(int count) =>
        Enumerable.Range(1, count)
            .Select(amount => new Entry(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), amount, TestLedger.BaseTime))
            .ToList();

    [Fact]
    public void From_MoreFetchedThanLimit_TrimsAndPointsAtTheLastItem()
    {
        var fetched = Entries(3);

        var page = EntryPage.From(fetched, limit: 2);

        Assert.Equal(fetched.Take(2), page.Items);
        Assert.Equal(fetched[1].Id, page.NextCursor);
    }

    [Fact]
    public void From_FetchedEqualsLimit_HasNoNextCursor()
    {
        var fetched = Entries(2);

        var page = EntryPage.From(fetched, limit: 2);

        Assert.Equal(fetched, page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void From_NothingFetched_IsEmpty()
    {
        var page = EntryPage.From([], limit: 2);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }
}
