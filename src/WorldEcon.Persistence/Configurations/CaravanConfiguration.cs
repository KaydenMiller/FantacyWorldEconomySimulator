using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class CaravanConfiguration : IEntityTypeConfiguration<Caravan>
{
    public void Configure(EntityTypeBuilder<Caravan> b)
    {
        b.ToTable("caravans");
        b.HasKey(x => x.Id);
        b.Property(x => x.UnitCostBasis).HasConversion<MoneyConverter>();
        b.Property(x => x.DepartTick).HasConversion<TickConverter>();
        b.Property(x => x.ArriveTick).HasConversion<TickConverter>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.OwnerId);
        b.HasIndex(x => x.DestinationId);
        b.HasIndex(x => x.ArriveTick);
        b.Ignore(x => x.DomainEvents);
        b.Ignore(x => x.ReturnTick);
        b.Ignore(x => x.LengthMetres);
        b.OwnsMany(x => x.Legs, l =>
        {
            l.ToTable("caravan_legs");
            l.WithOwner().HasForeignKey("CaravanId");
            l.HasKey("CaravanId", nameof(CaravanLeg.Sequence));
            l.Property(x => x.Sequence).ValueGeneratedNever();
            l.Property(x => x.EnterTick).HasConversion<TickConverter>();
            l.Property(x => x.ExitTick).HasConversion<TickConverter>();
            l.Property(x => x.Direction).HasConversion<string>();
            l.HasIndex(x => x.RouteId);
        });
        b.Navigation(x => x.Legs).HasField("_legs").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
