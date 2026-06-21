namespace WorldEcon.SharedKernel.Domain;

/// <summary>Base class for entities with identity equality (same concrete type + same Id).</summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : struct, IStronglyTypedId
{
    public TId Id { get; }

    protected Entity(TId id) => Id = id;

    public bool Equals(Entity<TId>? other)
        => other is not null && other.GetType() == GetType() && other.Id.Equals(Id);

    public override bool Equals(object? obj) => obj is Entity<TId> e && Equals(e);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
