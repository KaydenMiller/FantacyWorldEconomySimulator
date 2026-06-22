using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorldEcon.Domain.Logging;

namespace WorldEcon.Persistence.Configurations;

public sealed class LogEventScopeConfiguration : IEntityTypeConfiguration<LogEventScope>
{
    public void Configure(EntityTypeBuilder<LogEventScope> b)
    {
        b.ToTable("log_event_scopes");
        b.HasKey(x => x.Id);
        // Required: LogEventScope has no navigation to LogEvent, so EF does not infer WorldId by
        // convention here (unlike aggregates EF reaches via a relationship). Mapping it explicitly
        // keeps the WorldId column — which the migration's data-copy SQL and scope reads depend on.
        b.Property(x => x.WorldId);
        b.Property(x => x.ScopeKind).HasConversion<string>();
        b.HasIndex(x => x.LogEventId);
        // The hot read: events visible at a given scope, newest first.
        b.HasIndex(x => new { x.ScopeKind, x.ScopeId, x.Sequence });
        b.Ignore(x => x.DomainEvents);
    }
}
