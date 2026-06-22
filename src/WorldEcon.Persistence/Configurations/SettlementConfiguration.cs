using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Configurations;

public sealed class SettlementConfiguration : IEntityTypeConfiguration<Settlement>
{
    public void Configure(EntityTypeBuilder<Settlement> b)
    {
        b.ToTable("settlements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Type).HasConversion<string>();
        b.Property(x => x.State).HasConversion<string>();
        b.Property(x => x.Provenance).HasConversion<string>();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.RegionId);
        b.Ignore(x => x.DomainEvents);
    }
}
