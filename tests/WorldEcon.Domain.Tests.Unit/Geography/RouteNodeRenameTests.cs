using FluentAssertions;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class RouteNodeRenameTests
{
    private static RouteNode Junction(string? name = null) => RouteNode.Create(WorldId.New(), RouteNodeKind.Junction, 7, null, name).Value;

    [Test]
    public void Rename_TrimsAndStores()
    {
        var n = Junction();
        n.Rename("  Old Ford  ").IsError.Should().BeFalse();
        n.Name.Should().Be("Old Ford");
    }

    [Test]
    public void Rename_Null_Clears()
    {
        var n = Junction("Old Ford");
        n.Rename(null).IsError.Should().BeFalse();
        n.Name.Should().BeNull();
    }

    [Test]
    public void Rename_WhitespaceOnly_IsEmptyError()
    {
        var n = Junction("Keep");
        n.Rename("   ").FirstError.Code.Should().Be("routenode.name.empty");
        n.Name.Should().Be("Keep");
    }

    [Test]
    public void Rename_64Chars_Accepted_65_TooLong()
    {
        var n = Junction();
        n.Rename(new string('a', RouteNode.MaxNameLength)).IsError.Should().BeFalse();
        n.Rename(new string('b', 65)).FirstError.Code.Should().Be("routenode.name.tooLong");
        n.Name.Should().Be(new string('a', 64));
    }

    [Test]
    [Arguments("A\nB")]
    [Arguments("A\tB")]
    [Arguments("A\u0007B")]
    public void Rename_ControlCharacter_Rejected(string name)
        => Junction().Rename(name).FirstError.Code.Should().Be("routenode.name.control");

    [Test]
    public void Rename_NonAscii_Accepted()
    {
        var n = Junction();
        n.Rename("Ærøskøbing Kreuz").IsError.Should().BeFalse();
        n.Name.Should().Be("Ærøskøbing Kreuz");
    }

    [Test]
    public void Rename_SettlementNode_Rejected()
    {
        var n = RouteNode.Create(WorldId.New(), RouteNodeKind.Settlement, 1, SettlementId.New(), "Town").Value;
        n.Rename("Other").FirstError.Code.Should().Be("routenode.name.settlement");
        n.Name.Should().Be("Town");
    }

    [Test]
    public void Create_JunctionNameTooLong_Error()
        => RouteNode.Create(WorldId.New(), RouteNodeKind.Site, 1, null, new string('q', 65)).FirstError.Code
            .Should().Be("routenode.name.tooLong");

    [Test]
    public void Create_JunctionName_Trimmed()
        => RouteNode.Create(WorldId.New(), RouteNodeKind.Junction, 1, null, " Fork ").Value.Name.Should().Be("Fork");

    [Test]
    public void Create_SettlementLongName_Allowed()
        => RouteNode.Create(WorldId.New(), RouteNodeKind.Settlement, 1, SettlementId.New(), new string('s', 100)).IsError
            .Should().BeFalse();

    [Test]
    public void Rename_Site_Allowed()
    {
        var n = RouteNode.Create(WorldId.New(), RouteNodeKind.Site, 3, null, "Quarry").Value;
        n.Rename("Greystone Quarry").IsError.Should().BeFalse();
        n.Name.Should().Be("Greystone Quarry");
    }
}
