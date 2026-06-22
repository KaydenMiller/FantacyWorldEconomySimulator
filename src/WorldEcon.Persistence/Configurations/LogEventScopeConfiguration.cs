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
        b.Property(x => x.WorldId);
        b.Property(x => x.ScopeKind).HasConversion<string>();
        b.HasIndex(x => x.LogEventId);
        // The hot read: events visible at a given scope, newest first.
        b.HasIndex(x => new { x.ScopeKind, x.ScopeId, x.Sequence });
        b.Ignore(x => x.DomainEvents);
    }
}
