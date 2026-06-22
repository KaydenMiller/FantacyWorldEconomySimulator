using WorldEcon.Domain.Logging;

namespace WorldEcon.Application.Logging;

/// <summary>An on-demand rollup of the events visible at a scope within a tick window.</summary>
public sealed record ScopeSummary(
    LogScopeKind ScopeKind,
    Guid ScopeId,
    long FromTick,
    long ToTick,
    int TotalEvents,
    IReadOnlyDictionary<LogEventType, int> CountByType,
    IReadOnlyList<LogEvent> MajorEvents);   // Major+ events in the window (Magnitude >= Major), newest first
