using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class RepresentativeMerchantConfiguration : IEntityTypeConfiguration<RepresentativeMerchant>
{
    public void Configure(EntityTypeBuilder<RepresentativeMerchant> b)
    {
        b.ToTable("merchants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Capital).HasConversion<MoneyConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.Seat);
        b.Ignore(x => x.DomainEvents);
    }
}
