using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Configurations;

public sealed class RouteConfiguration : IEntityTypeConfiguration<Route>
{
    public void Configure(EntityTypeBuilder<Route> b)
    {
        b.ToTable("routes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Terrain).HasConversion<string>();
        b.Property(x => x.Category).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.FromSettlementId);
        b.HasIndex(x => x.ToSettlementId);
        b.Ignore(x => x.DomainEvents);
    }
}
