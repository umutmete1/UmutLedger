using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Infrastructure.Persistence.Configurations;

public sealed class TransferConfiguration : IEntityTypeConfiguration<Transfer>
{
    public void Configure(EntityTypeBuilder<Transfer> builder)
    {
        builder.ToTable("transfers", table =>
        {
            table.HasCheckConstraint("ck_transfers_amount_positive", "amount > 0");
            table.HasCheckConstraint("ck_transfers_distinct_accounts", "debit_account_id <> credit_account_id");
            table.HasCheckConstraint("ck_transfers_currency", CurrencyCheck.Sql("currency"));
        });

        builder.HasKey(transfer => transfer.Id).HasName("pk_transfers");
        builder.Property(transfer => transfer.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(transfer => transfer.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasConversion(key => key.Value, value => new IdempotencyKey(value));
        builder.Property(transfer => transfer.DebitAccountId).HasColumnName("debit_account_id");
        builder.Property(transfer => transfer.CreditAccountId).HasColumnName("credit_account_id");
        builder.Property(transfer => transfer.Amount).HasColumnName("amount");
        builder.Property(transfer => transfer.Currency).HasColumnName("currency").HasConversion<string>();
        builder.Property(transfer => transfer.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(transfer => transfer.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName(LedgerConstraints.TransferIdempotencyKey);

        builder.HasOne<Account>().WithMany()
            .HasForeignKey(transfer => transfer.DebitAccountId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_transfers_debit_account");
        builder.HasOne<Account>().WithMany()
            .HasForeignKey(transfer => transfer.CreditAccountId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_transfers_credit_account");
        builder.HasMany(transfer => transfer.Entries).WithOne()
            .HasForeignKey(entry => entry.TransferId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_entries_transfer");
    }
}
