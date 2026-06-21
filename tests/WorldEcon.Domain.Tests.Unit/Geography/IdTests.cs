using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class IdTests
{
    [Test]
    public void New_ProducesUniqueNonEmptyIds()
    {
        var a = SettlementId.New();
        var b = SettlementId.New();
        a.Should().NotBe(b);
        a.Value.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void Ids_AreStronglyTyped()
        => WorldId.New().Should().BeAssignableTo<IStronglyTypedId>();
}
