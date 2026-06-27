using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class ShopPriceBeliefConfiguration : IEntityTypeConfiguration<ShopPriceBelief>
{
    public void Configure(EntityTypeBuilder<ShopPriceBelief> b)
    {
        b.ToTable("shop_price_beliefs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Low).HasConversion<MoneyConverter>();
        b.Property(x => x.High).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        // The hot read: a settlement's shops' beliefs for a good — look up by (shop, good).
        b.HasIndex(x => new { x.ShopId, x.GoodId });
        b.Ignore(x => x.DomainEvents);
    }
}
