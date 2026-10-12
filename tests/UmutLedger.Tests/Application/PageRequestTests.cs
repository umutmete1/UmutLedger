using UmutLedger.Application.Accounts;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Tests.Application;

public class PageRequestTests
{
    [Fact]
    public void Constructor_NoLimit_UsesTheDefault()
    {
        var page = new PageRequest(null, null);

        Assert.Equal(PageRequest.DefaultLimit, page.Limit);
        Assert.Null(page.Cursor);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(PageRequest.MaxLimit)]
    public void Constructor_LimitInRange_IsAccepted(int limit)
    {
        var cursor = Guid.CreateVersion7();

        var page = new PageRequest(limit, cursor);

        Assert.Equal(limit, page.Limit);
        Assert.Equal(cursor, page.Cursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PageRequest.MaxLimit + 1)]
    public void Constructor_LimitOutOfRange_Throws(int limit)
    {
        Assert.Throws<ValidationFailedException>(() => new PageRequest(limit, null));
    }
}
