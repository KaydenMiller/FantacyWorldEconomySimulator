using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Configurations;

public sealed class PoliticalRegionConfiguration : IEntityTypeConfiguration<PoliticalRegion>
{
    public void Configure(EntityTypeBuilder<PoliticalRegion> b)
    {
        b.ToTable("political_regions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.CountryId);
        b.Ignore(x => x.DomainEvents);
    }
}
