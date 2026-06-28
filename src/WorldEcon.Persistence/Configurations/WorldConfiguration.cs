using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class WorldConfiguration : IEntityTypeConfiguration<World>
{
    public void Configure(EntityTypeBuilder<World> b)
    {
        b.ToTable("worlds");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Seed).HasConversion<UInt64Converter>();
        b.Property(x => x.CurrentTick).HasConversion<TickConverter>();
        b.Property(x => x.Calendar).HasConversion<CalendarDefinitionConverter>();
        b.Property(x => x.Currency).HasConversion<CurrencyDefinitionConverter>();
        b.Property(x => x.RulesetVersion).IsRequired();
        b.Property(x => x.DisplayUnitSystem).HasConversion<string>();
        b.Ignore(x => x.DomainEvents);
    }
}
