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
        b.Property(x => x.WeightCapacity).HasConversion<MassConverter>();
        b.Property(x => x.VolumeCapacity).HasConversion<VolumeConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.Seat);
        b.Ignore(x => x.DomainEvents);
        b.OwnsMany(x => x.Trips, t =>
        {
            t.ToTable("merchant_trips");
            t.WithOwner().HasForeignKey("MerchantId");
            t.HasKey("MerchantId", nameof(MerchantTrip.Slot));
            t.Property(x => x.Slot).ValueGeneratedNever();
        });
        b.Navigation(x => x.Trips).HasField("_trips").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
