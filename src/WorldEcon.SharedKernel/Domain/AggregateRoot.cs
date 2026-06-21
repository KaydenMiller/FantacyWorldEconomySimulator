namespace WorldEcon.SharedKernel.Domain;

/// <summary>An entity that is the root of an aggregate and can raise in-process domain events.</summary>
public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id)
    where TId : struct, IStronglyTypedId
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
