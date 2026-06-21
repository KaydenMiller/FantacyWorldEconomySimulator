namespace WorldEcon.SharedKernel.Domain;

/// <summary>Marker for a strongly-typed, Guid-backed identifier.</summary>
public interface IStronglyTypedId
{
    Guid Value { get; }
}
