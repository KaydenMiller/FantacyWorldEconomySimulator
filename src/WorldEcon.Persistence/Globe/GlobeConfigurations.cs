using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Globe;

// Table and column names here are also used verbatim by GlobeStore's raw INSERT statements.

public sealed class GlobeCellRowConfiguration : IEntityTypeConfiguration<GlobeCellRow>
{
    public void Configure(EntityTypeBuilder<GlobeCellRow> b)
    {
        b.ToTable("globe_cells");
        b.HasKey(x => new { x.WorldId, x.CellId });
    }
}

public sealed class GlobeCellNeighbourRowConfiguration : IEntityTypeConfiguration<GlobeCellNeighbourRow>
{
    public void Configure(EntityTypeBuilder<GlobeCellNeighbourRow> b)
    {
        b.ToTable("globe_cell_neighbours");
        b.HasKey(x => new { x.WorldId, x.CellId, x.Slot });
    }
}

public sealed class PlateRowConfiguration : IEntityTypeConfiguration<PlateRow>
{
    public void Configure(EntityTypeBuilder<PlateRow> b)
    {
        b.ToTable("plates");
        b.HasKey(x => new { x.WorldId, x.PlateId });
    }
}

public sealed class GlobeCellCompositionRowConfiguration : IEntityTypeConfiguration<GlobeCellCompositionRow>
{
    public void Configure(EntityTypeBuilder<GlobeCellCompositionRow> b)
    {
        b.ToTable("globe_cell_composition");
        b.HasKey(x => new { x.WorldId, x.CellId, x.Slot });
    }
}

public sealed class RiverEdgeRowConfiguration : IEntityTypeConfiguration<RiverEdgeRow>
{
    public void Configure(EntityTypeBuilder<RiverEdgeRow> b)
    {
        b.ToTable("river_edges");
        b.HasKey(x => new { x.WorldId, x.LowCellId, x.HighCellId });
    }
}

public sealed class WorldGenRunRowConfiguration : IEntityTypeConfiguration<WorldGenRunRow>
{
    public void Configure(EntityTypeBuilder<WorldGenRunRow> b)
    {
        b.ToTable("worldgen_runs");
        b.HasKey(x => x.WorldId);
        b.Property(x => x.SettingsJson).IsRequired();
        b.Property(x => x.StagesJson).IsRequired();
    }
}

public sealed class GeographicRegionConfiguration : IEntityTypeConfiguration<GeographicRegion>
{
    public void Configure(EntityTypeBuilder<GeographicRegion> b)
    {
        b.ToTable("geographic_regions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class GeographicRegionCellConfiguration : IEntityTypeConfiguration<GeographicRegionCell>
{
    public void Configure(EntityTypeBuilder<GeographicRegionCell> b)
    {
        // Same invariant as GeographicRegionCell.Create; rows written in bulk by seeding bypass the domain factory.
        b.ToTable("geographic_region_cells", t => t.HasCheckConstraint("CK_geographic_region_cells_CoverageBp", "\"CoverageBp\" BETWEEN 1 AND 10000"));
        b.HasKey(x => new { x.GeographicRegionId, x.Cell });
        b.Property(x => x.Cell).HasColumnName("CellId");
        // "Which areas contain this cell?" lookups (globe cell <id>).
        b.HasIndex(x => x.Cell);
    }
}

public sealed class GeographicRegionContainmentConfiguration : IEntityTypeConfiguration<GeographicRegionContainment>
{
    public void Configure(EntityTypeBuilder<GeographicRegionContainment> b)
    {
        b.ToTable("geographic_region_containments");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.ParentRegionId);
        b.HasIndex(x => x.ChildRegionId);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class TileBlockRowConfiguration : IEntityTypeConfiguration<TileBlockRow>
{
    public void Configure(EntityTypeBuilder<TileBlockRow> b)
    {
        b.ToTable("tile_blocks");
        b.HasKey(x => x.CellId);
        b.Property(x => x.CellId).ValueGeneratedNever();
        b.Property(x => x.Data).IsRequired();
    }
}

public sealed class LakeRowConfiguration : IEntityTypeConfiguration<LakeRow>
{
    public void Configure(EntityTypeBuilder<LakeRow> b)
    {
        b.ToTable("lakes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
    }
}

public sealed class MapImageryRowConfiguration : IEntityTypeConfiguration<MapImageryRow>
{
    public void Configure(EntityTypeBuilder<MapImageryRow> b)
    {
        b.ToTable("map_imagery", t => t.HasCheckConstraint("CK_map_imagery_Id", "\"Id\" IN (0, 1)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Data).IsRequired();
    }
}

public sealed class GeologyRowConfiguration : IEntityTypeConfiguration<GeologyRow>
{
    public void Configure(EntityTypeBuilder<GeologyRow> b)
    {
        b.ToTable("geology", t => t.HasCheckConstraint("CK_geology_Id", "\"Id\" = 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Data).IsRequired();
    }
}

public sealed class RegionLevelRowConfiguration : IEntityTypeConfiguration<RegionLevelRow>
{
    public void Configure(EntityTypeBuilder<RegionLevelRow> b)
    {
        b.ToTable("region_level", t => t.HasCheckConstraint("CK_region_level_Id", "\"Id\" = 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Data).IsRequired();
    }
}
