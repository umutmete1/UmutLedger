using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;

namespace UmutLedger.Infrastructure.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    private static readonly DateTimeOffset SeededAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", table =>
        {
            table.HasCheckConstraint("ck_accounts_customer_balance_non_negative", "type = 'System' OR balance >= 0");
            table.HasCheckConstraint("ck_accounts_name_not_empty", "length(trim(name)) > 0");
            table.HasCheckConstraint("ck_accounts_currency", CurrencyCheck.Sql("currency"));
            table.HasCheckConstraint("ck_accounts_type", "type IN ('Customer', 'System')");
        });

        builder.HasKey(account => account.Id).HasName("pk_accounts");
        builder.Property(account => account.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(account => account.Name).HasColumnName("name");
        builder.Property(account => account.Currency).HasColumnName("currency").HasConversion<string>();
        builder.Property(account => account.Type).HasColumnName("type").HasConversion<string>();
        builder.Property(account => account.Balance).HasColumnName("balance");
        builder.Property(account => account.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(account => account.Currency)
            .IsUnique()
            .HasFilter("type = 'System'")
            .HasDatabaseName("ux_accounts_system_currency");

        builder.HasData(
            WorldAccount("00000000-0000-7000-8000-000000000001", Currency.USD),
            WorldAccount("00000000-0000-7000-8000-000000000002", Currency.EUR),
            WorldAccount("00000000-0000-7000-8000-000000000003", Currency.GBP));
    }

    private static object WorldAccount(string id, Currency currency) => new
    {
        Id = Guid.Parse(id),
        Name = $"World {currency}",
        Currency = currency,
        Type = AccountType.System,
        Balance = 0L,
        CreatedAt = SeededAt
    };
}
