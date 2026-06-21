using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Actions;

public readonly record struct DmActionId(Guid Value) : IStronglyTypedId { public static DmActionId New() => new(Guid.NewGuid()); }
