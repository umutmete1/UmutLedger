using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UmutLedger.Domain.Entities;

namespace UmutLedger.Infrastructure.Persistence.Configurations;

public sealed class EntryConfiguration : IEntityTypeConfiguration<Entry>
{
    public void Configure(EntityTypeBuilder<Entry> builder)
    {
        builder.ToTable("entries", table =>
            table.HasCheckConstraint("ck_entries_amount_not_zero", "amount <> 0"));

        builder.HasKey(entry => entry.Id).HasName("pk_entries");
        builder.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(entry => entry.TransferId).HasColumnName("transfer_id");
        builder.Property(entry => entry.AccountId).HasColumnName("account_id");
        builder.Property(entry => entry.Amount).HasColumnName("amount");
        builder.Property(entry => entry.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(entry => new { entry.AccountId, entry.Id }).HasDatabaseName("ix_entries_account_id_id");

        builder.HasOne<Account>().WithMany()
            .HasForeignKey(entry => entry.AccountId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_entries_account");
    }
}
