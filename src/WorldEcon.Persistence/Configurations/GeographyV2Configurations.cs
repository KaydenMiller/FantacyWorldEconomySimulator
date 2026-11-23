using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Configurations;

public sealed class PoliticalRegionContinentConfiguration : IEntityTypeConfiguration<PoliticalRegionContinent>
{
    public void Configure(EntityTypeBuilder<PoliticalRegionContinent> b)
    {
        b.ToTable("political_region_continents");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.PoliticalRegionId);
        b.HasIndex(x => x.ContinentId);
        b.Ignore(x => x.DomainEvents);
    }
}

public sealed class TerritorialClaimConfiguration : IEntityTypeConfiguration<TerritorialClaim>
{
    public void Configure(EntityTypeBuilder<TerritorialClaim> b)
    {
        b.ToTable("territorial_claims");
        b.HasKey(x => x.Id);
        b.Property(x => x.ClaimType).HasConversion<string>();
        b.Property(x => x.TargetKind).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.CountryId);
        b.HasIndex(x => new { x.TargetKind, x.TargetId });
        b.Ignore(x => x.DomainEvents);
    }
}
