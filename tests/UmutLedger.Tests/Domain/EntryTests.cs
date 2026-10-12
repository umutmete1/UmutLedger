using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Tests.Domain;

public class EntryTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_ValidValues_ExposesThem()
    {
        var id = Guid.CreateVersion7();
        var transferId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();

        var entry = new Entry(id, transferId, accountId, -100, CreatedAt);

        Assert.Equal(id, entry.Id);
        Assert.Equal(transferId, entry.TransferId);
        Assert.Equal(accountId, entry.AccountId);
        Assert.Equal(-100, entry.Amount);
        Assert.Equal(CreatedAt, entry.CreatedAt);
    }

    [Fact]
    public void Constructor_ZeroAmount_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Entry(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 0, CreatedAt));
    }

    [Fact]
    public void Constructor_EmptyId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Entry(Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), 1, CreatedAt));
    }

    [Fact]
    public void Constructor_EmptyTransferId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Entry(Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), 1, CreatedAt));
    }

    [Fact]
    public void Constructor_EmptyAccountId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Entry(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, 1, CreatedAt));
    }
}
