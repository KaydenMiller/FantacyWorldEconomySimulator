using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Actions;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class DmActionConfiguration : IEntityTypeConfiguration<DmAction>
{
    public void Configure(EntityTypeBuilder<DmAction> b)
    {
        b.ToTable("dm_actions");
        b.HasKey(x => x.Id);
        b.Property(x => x.AppliedTick).HasConversion<TickConverter>();
        b.Property(x => x.Kind).HasConversion<string>();
        b.Property(x => x.Description).IsRequired();
        b.Property(x => x.ArgsJson).IsRequired(); // required but may be empty string
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => new { x.WorldId, x.Sequence });
        b.Ignore(x => x.DomainEvents);
    }
}
