using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class TradeEntitiesTests
{
    private static RepresentativeMerchant NewMerchant(long capital = 1_000, long cargo = 100, long reach = 5)
        => RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), new Money(capital), cargo, reach).Value;

    // ---- RepresentativeMerchant.Create ----

    [Test]
    public void Merchant_Create_SetsFields()
    {
        var world = WorldId.New();
        var seat = SettlementId.New();
        var m = RepresentativeMerchant.Create(world, seat, new Money(2_500), cargoCapacity: 50, reach: 3).Value;
        m.WorldId.Should().Be(world);
        m.Seat.Should().Be(seat);
        m.Capital.Should().Be(new Money(2_500));
        m.CargoCapacity.Should().Be(50);
        m.Reach.Should().Be(3);
        m.Id.Value.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void Merchant_Create_RejectsNegativeCapital()
        => RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), new Money(-1), 10, 1).IsError.Should().BeTrue();

    [Test]
    public void Merchant_Create_RejectsCargoCapacityBelowOne()
        => RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), Money.Zero, 0, 1).IsError.Should().BeTrue();

    [Test]
    public void Merchant_Create_RejectsReachBelowOne()
        => RepresentativeMerchant.Create(WorldId.New(), SettlementId.New(), Money.Zero, 1, 0).IsError.Should().BeTrue();

    [Test]
    public void Merchant_ImplementsIMerchantAgent()
        => NewMerchant().Should().BeAssignableTo<IMerchantAgent>();

    // ---- Spend / Earn ----

    [Test]
    public void Merchant_Spend_ReducesCapital()
    {
        var m = NewMerchant(1_000);
        m.Spend(new Money(300));
        m.Capital.Should().Be(new Money(700));
    }

    [Test]
    public void Merchant_Spend_MoreThanCapital_Throws_AndDoesNotMutate()
    {
        var m = NewMerchant(1_000);
        var act = () => m.Spend(new Money(1_001));
        act.Should().Throw<InvalidOperationException>();
        m.Capital.Should().Be(new Money(1_000));
    }

    [Test]
    public void Merchant_Spend_Negative_Throws()
    {
        var m = NewMerchant(1_000);
        var act = () => m.Spend(new Money(-1));
        act.Should().Throw<InvalidOperationException>();
        m.Capital.Should().Be(new Money(1_000));
    }

    [Test]
    public void Merchant_Earn_IncreasesCapital()
    {
        var m = NewMerchant(1_000);
        m.Earn(new Money(250));
        m.Capital.Should().Be(new Money(1_250));
    }

    [Test]
    public void Merchant_Earn_Negative_Throws()
    {
        var m = NewMerchant(1_000);
        var act = () => m.Earn(new Money(-1));
        act.Should().Throw<InvalidOperationException>();
        m.Capital.Should().Be(new Money(1_000));
    }

    // ---- Caravan.Create ----

    private static ErrorOr.ErrorOr<Caravan> CreateCaravan(
        long quantity = 10, long unitCost = 5, long depart = 0, long arrive = 100,
        SettlementId? origin = null, SettlementId? destination = null)
        => Caravan.Create(
            WorldId.New(), MerchantId.New(),
            origin ?? SettlementId.New(),
            destination ?? SettlementId.New(),
            GoodId.New(), quantity, new Money(unitCost),
            new Tick(depart), new Tick(arrive));

    [Test]
    public void Caravan_Create_SetsFields()
    {
        var world = WorldId.New();
        var owner = MerchantId.New();
        var origin = SettlementId.New();
        var dest = SettlementId.New();
        var good = GoodId.New();
        var c = Caravan.Create(world, owner, origin, dest, good, 42, new Money(7), new Tick(10), new Tick(60)).Value;
        c.WorldId.Should().Be(world);
        c.OwnerId.Should().Be(owner);
        c.OriginId.Should().Be(origin);
        c.DestinationId.Should().Be(dest);
        c.GoodId.Should().Be(good);
        c.Quantity.Should().Be(42);
        c.UnitCostBasis.Should().Be(new Money(7));
        c.DepartTick.Should().Be(new Tick(10));
        c.ArriveTick.Should().Be(new Tick(60));
        c.Delivered.Should().BeFalse();
    }

    [Test]
    public void Caravan_Create_RejectsQuantityBelowOne()
        => CreateCaravan(quantity: 0).IsError.Should().BeTrue();

    [Test]
    public void Caravan_Create_RejectsNegativeUnitCost()
        => CreateCaravan(unitCost: -1).IsError.Should().BeTrue();

    [Test]
    public void Caravan_Create_RejectsArriveNotAfterDepart()
        => CreateCaravan(depart: 100, arrive: 100).IsError.Should().BeTrue();

    [Test]
    public void Caravan_Create_RejectsArriveBeforeDepart()
        => CreateCaravan(depart: 100, arrive: 50).IsError.Should().BeTrue();

    [Test]
    public void Caravan_Create_RejectsSameOriginAndDestination()
    {
        var s = SettlementId.New();
        CreateCaravan(origin: s, destination: s).IsError.Should().BeTrue();
    }

    // ---- MarkDelivered ----

    [Test]
    public void Caravan_MarkDelivered_SetsDelivered()
    {
        var c = CreateCaravan().Value;
        c.MarkDelivered();
        c.Delivered.Should().BeTrue();
    }

    [Test]
    public void Caravan_MarkDelivered_Twice_Throws()
    {
        var c = CreateCaravan().Value;
        c.MarkDelivered();
        var act = () => c.MarkDelivered();
        act.Should().Throw<InvalidOperationException>();
    }
}
