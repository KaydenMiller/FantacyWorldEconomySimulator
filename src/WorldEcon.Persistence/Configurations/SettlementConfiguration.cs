using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Globe;

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
        b.Property(x => x.CellId).HasConversion(
            v => v == null ? (int?)null : v.Value.Value,
            v => v == null ? (GlobeCellId?)null : new GlobeCellId(v.Value));
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => x.PoliticalRegionId);
        b.Ignore(x => x.DomainEvents);
    }
}
