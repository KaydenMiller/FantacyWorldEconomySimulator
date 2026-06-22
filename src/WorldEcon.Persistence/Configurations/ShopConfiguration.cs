using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class ShopConfiguration : IEntityTypeConfiguration<Shop>
{
    public void Configure(EntityTypeBuilder<Shop> b)
    {
        b.ToTable("shops");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Till).HasConversion<MoneyConverter>();
        b.Property(x => x.Kind).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.SettlementId);
        b.HasIndex(x => new { x.SettlementId, x.Kind });
        b.Ignore(x => x.DomainEvents);
    }
}
