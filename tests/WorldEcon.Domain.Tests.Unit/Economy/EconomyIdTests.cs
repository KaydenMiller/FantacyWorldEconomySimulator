using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class EconomyIdTests
{
    [Test]
    public void New_ProducesUniqueNonEmptyIds()
    {
        GoodId.New().Should().NotBe(GoodId.New());
        GoodId.New().Value.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void Ids_AreStronglyTyped()
        => ShopId.New().Should().BeAssignableTo<IStronglyTypedId>();
}
