using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Logging;

public readonly record struct LogEventId(Guid Value) : IStronglyTypedId { public static LogEventId New() => new(Guid.NewGuid()); }
public readonly record struct LogEventScopeId(Guid Value) : IStronglyTypedId { public static LogEventScopeId New() => new(Guid.NewGuid()); }
