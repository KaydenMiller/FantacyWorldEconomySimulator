using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class StockpileConfiguration : IEntityTypeConfiguration<Stockpile>
{
    public void Configure(EntityTypeBuilder<Stockpile> b)
    {
        b.ToTable("stockpiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.OwnerKind).HasConversion<string>();
        b.Property(x => x.CostBasis).HasConversion<MoneyConverter>();
        b.Property(x => x.MarketPrice).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => new { x.OwnerKind, x.OwnerId, x.GoodId });
        b.Ignore(x => x.DomainEvents);
    }
}
