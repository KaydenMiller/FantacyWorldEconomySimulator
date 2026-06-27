using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class MoneyLedgerSnapshotConfiguration : IEntityTypeConfiguration<MoneyLedgerSnapshot>
{
    public void Configure(EntityTypeBuilder<MoneyLedgerSnapshot> b)
    {
        b.ToTable("money_ledger_snapshots");
        b.HasKey(x => x.Id);
        b.Property(x => x.Tick).HasConversion<TickConverter>();
        b.Property(x => x.TotalSupply).HasConversion<MoneyConverter>();
        b.Property(x => x.NetDelta).HasConversion<MoneyConverter>();
        b.Property(x => x.Discrepancy).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => new { x.WorldId, x.Sequence });
        b.Ignore(x => x.DomainEvents);
    }
}
