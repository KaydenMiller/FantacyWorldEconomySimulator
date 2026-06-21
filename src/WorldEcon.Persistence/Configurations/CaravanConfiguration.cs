using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Economy;
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
    }
}
