using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class PathfinderTests
{
    private static readonly WorldId World = WorldId.New();
    private static readonly SettlementId A = SettlementId.New();
    private static readonly SettlementId B = SettlementId.New();
    private static readonly SettlementId C = SettlementId.New();
    private static readonly SettlementId D = SettlementId.New();

    private static Route Edge(SettlementId from, SettlementId to, long distance)
        => Route.Create(World, from, to, distance, Terrain.Plains, danger: 0, RouteCategory.Land).Value;

    [Test]
    public void FindPath_LinearChain_SumsDistance()
    {
        var pf = new Pathfinder(new[] { Edge(A, B, 100), Edge(B, C, 50) });

        var path = pf.FindPath(A, C);

        path.Should().NotBeNull();
        path!.Nodes.Should().Equal(A, B, C);
        path.TotalDistance.Should().Be(150);
    }

    [Test]
    public void FindPath_PicksShorterOfTwoRoutes()
    {
        var pf = new Pathfinder(new[] { Edge(A, B, 100), Edge(A, C, 10), Edge(C, B, 10) });

        var path = pf.FindPath(A, B);

        path.Should().NotBeNull();
        path!.TotalDistance.Should().Be(20);
        path.Nodes.Should().Equal(A, C, B);
    }

    [Test]
    public void FindPath_Unreachable_ReturnsNull()
    {
        var pf = new Pathfinder(new[] { Edge(A, B, 100) });

        pf.FindPath(B, A).Should().BeNull();
    }

    [Test]
    public void FindPath_SameNode_ReturnsZero()
    {
        var pf = new Pathfinder(new[] { Edge(A, B, 100) });

        var path = pf.FindPath(A, A);

        path.Should().NotBeNull();
        path!.Nodes.Should().Equal(A);
        path.TotalDistance.Should().Be(0);
    }

    [Test]
    public void FindReachable_RespectsMaxDistance()
    {
        var pf = new Pathfinder(new[] { Edge(A, B, 100), Edge(B, C, 100) });

        var atHundred = pf.FindReachable(A, 100);
        atHundred.Should().ContainSingle();
        atHundred[0].Settlement.Should().Be(B);
        atHundred[0].Distance.Should().Be(100);

        var atTwoFifty = pf.FindReachable(A, 250);
        atTwoFifty.Should().HaveCount(2);
        atTwoFifty[0].Settlement.Should().Be(B);
        atTwoFifty[0].Distance.Should().Be(100);
        atTwoFifty[1].Settlement.Should().Be(C);
        atTwoFifty[1].Distance.Should().Be(200);
    }

    [Test]
    public void Pathfinding_IsDeterministic()
    {
        // Diamond: two equal-distance paths A->B->D and A->C->D, each 100.
        var pf = new Pathfinder(new[]
        {
            Edge(A, B, 50), Edge(A, C, 50), Edge(B, D, 50), Edge(C, D, 50),
        });

        var first = pf.FindPath(A, D);
        var second = pf.FindPath(A, D);

        first.Should().NotBeNull();
        first.Should().Be(second);
        first!.TotalDistance.Should().Be(100);
    }
}
