using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence.Conversions;

namespace WorldEcon.Persistence.Configurations;

public sealed class LogEventConfiguration : IEntityTypeConfiguration<LogEvent>
{
    public void Configure(EntityTypeBuilder<LogEvent> b)
    {
        b.ToTable("log_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.OccurredTick).HasConversion<TickConverter>();
        b.Property(x => x.Type).HasConversion<string>();
        b.Property(x => x.Magnitude).HasConversion<string>();
        b.Property(x => x.OriginKind).HasConversion<string>();
        b.Property(x => x.Message).IsRequired();
        b.Property(x => x.PayloadJson).IsRequired();
        b.HasIndex(x => x.WorldId);
        b.HasIndex(x => new { x.WorldId, x.Sequence });
        b.Ignore(x => x.DomainEvents);
    }
}
