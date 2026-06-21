using FluentAssertions;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit;

public class EntityBaseTests
{
    private readonly record struct FooId(Guid Value) : IStronglyTypedId;
    private readonly record struct BarId(Guid Value) : IStronglyTypedId;

    private sealed class Foo(FooId id) : Entity<FooId>(id);
    private sealed class Bar(BarId id) : Entity<BarId>(id);

    private sealed class FooAggregate(FooId id) : AggregateRoot<FooId>(id)
    {
        public void DoThing() => Raise(new ThingHappened());
    }
    private sealed record ThingHappened : IDomainEvent;

    [Test]
    public void Entities_WithSameTypeAndId_AreEqual()
    {
        var id = new FooId(Guid.NewGuid());
        new Foo(id).Should().Be(new Foo(id));
    }

    [Test]
    public void Entities_WithDifferentIds_AreNotEqual()
        => new Foo(new FooId(Guid.NewGuid())).Should().NotBe(new Foo(new FooId(Guid.NewGuid())));

    [Test]
    public void Entities_OfDifferentType_WithDistinctIds_AreNotEqual()
    {
        object foo = new Foo(new FooId(Guid.NewGuid()));
        object bar = new Bar(new BarId(Guid.NewGuid()));
        foo.Should().NotBe(bar);
    }

    [Test]
    public void RaisedEvents_AreExposed_AndClearable()
    {
        var agg = new FooAggregate(new FooId(Guid.NewGuid()));
        agg.DoThing();
        agg.DomainEvents.Should().ContainSingle();
        agg.ClearDomainEvents();
        agg.DomainEvents.Should().BeEmpty();
    }
}
