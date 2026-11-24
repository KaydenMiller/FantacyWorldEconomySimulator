using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class RouteConfiguration : IEntityTypeConfiguration<Route>
{
    public void Configure(EntityTypeBuilder<Route> b)
    {
        b.ToTable("routes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Category).HasConversion<string>();
        b.Property(x => x.Surface).HasConversion<string>();
        b.Property(x => x.Provenance).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.NodeA);
        b.HasIndex(x => x.NodeB);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class RouteNodeConfiguration : IEntityTypeConfiguration<RouteNode>
{
    public void Configure(EntityTypeBuilder<RouteNode> b)
    {
        // Junction and site names are limited to RouteNode.MaxNameLength (SQLite ignores column lengths, so it is a CHECK
        // constraint). A settlement node copies the settlement's name, which has no such limit.
        b.ToTable("route_nodes", t => t.HasCheckConstraint("CK_route_nodes_name_length",
            $"\"Kind\" = 'Settlement' OR \"Name\" IS NULL OR length(\"Name\") <= {RouteNode.MaxNameLength}"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasConversion<string>();
        b.Property(x => x.PortKind).HasConversion<string>();
        b.HasIndex(x => new { x.WorldId, x.TileId });
        b.HasIndex(x => x.SettlementId);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class RouteTileConfiguration : IEntityTypeConfiguration<RouteTile>
{
    public void Configure(EntityTypeBuilder<RouteTile> b)
    {
        b.ToTable("route_tiles");
        b.HasKey(x => new { x.RouteId, x.Sequence });
        b.HasIndex(x => x.TileId);
    }
}

public sealed class RoadConfiguration : IEntityTypeConfiguration<Road>
{
    public void Configure(EntityTypeBuilder<Road> b)
    {
        b.ToTable("roads");
        b.HasKey(x => x.Id);
        b.Property(x => x.Category).HasConversion<string>();
        b.Property(x => x.Provenance).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.Ignore(x => x.DomainEvents);
        b.OwnsMany(x => x.Routes, e =>
        {
            e.ToTable("road_routes");
            e.WithOwner().HasForeignKey("RoadId");
            e.HasKey("RoadId", nameof(RoadRouteEntry.Sequence));
            e.Property(x => x.Sequence).ValueGeneratedNever();
            e.Property(x => x.Direction).HasConversion<string>();
            e.HasIndex(x => x.RouteId);
        });
        b.Navigation(x => x.Routes).HasField("_routes").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class RouteUpgradeConfiguration : IEntityTypeConfiguration<RouteUpgrade>
{
    public void Configure(EntityTypeBuilder<RouteUpgrade> b)
    {
        b.ToTable("route_upgrades");
        b.Property(x => x.Tick).HasConversion<TickConverter>();
        b.HasKey(x => new { x.RouteId, x.Tick });
        b.Property(x => x.FromSurface).HasConversion<string>();
        b.Property(x => x.ToSurface).HasConversion<string>();
    }
}

public sealed class VoyagePathConfiguration : IEntityTypeConfiguration<VoyagePath>
{
    public void Configure(EntityTypeBuilder<VoyagePath> b)
    {
        b.ToTable("voyage_paths");
        b.HasKey(x => x.RouteId);
    }
}

public sealed class TravelKnowledgeConfiguration : IEntityTypeConfiguration<WorldEcon.Domain.Economy.TravelKnowledge>
{
    public void Configure(EntityTypeBuilder<WorldEcon.Domain.Economy.TravelKnowledge> b)
    {
        b.ToTable("travel_knowledge");
        b.HasKey(x => new { x.KnowerSettlementId, x.FromSettlementId, x.ToSettlementId, x.VehicleClass });
        b.Property(x => x.Source).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
    }
}

public sealed class VehicleClassConfiguration : IEntityTypeConfiguration<VehicleClass>
{
    public void Configure(EntityTypeBuilder<VehicleClass> b)
    {
        b.ToTable("vehicle_classes");
        b.HasKey(x => new { x.WorldId, x.Name });
        b.Property(x => x.Category).HasConversion<string>();
    }
}
