using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit;

public class ShopSubstrateTests
{
    private static readonly WorldId World = WorldId.New();
    private static readonly SettlementId Settle = SettlementId.New();

    [Test]
    public void Shop_DefaultKind_IsRetail()
    {
        var shop = Shop.Create(World, Settle, "The Sundries", 2000, new Money(100)).Value;
        shop.Kind.Should().Be(ShopKind.Retail);
    }

    [Test]
    public void Shop_CreateVendor_SetsKind()
    {
        var pub = Shop.CreateVendor(World, Settle, "Town Market", ShopKind.PublicMarket).Value;
        pub.Kind.Should().Be(ShopKind.PublicMarket);
        pub.MarkupBp.Should().Be(0);
        pub.Till.Should().Be(Money.Zero);
    }

    [Test]
    public void ProductionNode_AssignProducerShop_Sets()
    {
        var node = ProductionNode.Create(World, Settle, RecipeId.New(), FacilityType.Smithy, 1).Value;
        node.ProducerShopId.Should().BeNull();
        var shopId = ShopId.New();
        node.AssignProducerShop(shopId);
        node.ProducerShopId.Should().Be(shopId);
    }

    [Test]
    public void ResourceEndowment_AssignProducerShop_Sets()
    {
        var e = ResourceEndowment.Create(World, Settle, GoodId.New(), 30).Value;
        e.ProducerShopId.Should().BeNull();
        var shopId = ShopId.New();
        e.AssignProducerShop(shopId);
        e.ProducerShopId.Should().Be(shopId);
    }
}
