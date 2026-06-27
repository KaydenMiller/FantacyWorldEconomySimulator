using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class MoneyLedgerLineConfiguration : IEntityTypeConfiguration<MoneyLedgerLine>
{
    public void Configure(EntityTypeBuilder<MoneyLedgerLine> b)
    {
        b.ToTable("money_ledger_lines");
        b.HasKey(x => x.Id);
        // No navigation to the snapshot, so EF won't infer WorldId here — map it explicitly
        // (mirrors LogEventScope).
        b.Property(x => x.WorldId);
        b.Property(x => x.Channel).HasConversion<string>();
        b.Property(x => x.Kind).HasConversion<string>();
        b.Property(x => x.Amount).HasConversion<MoneyConverter>();
        // Cascade-delete FK: removing a snapshot removes its lines.
        b.HasOne<MoneyLedgerSnapshot>()
            .WithMany()
            .HasForeignKey(x => x.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.SnapshotId);
        b.Ignore(x => x.DomainEvents);
    }
}
