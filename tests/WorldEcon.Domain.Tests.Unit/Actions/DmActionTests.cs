using FluentAssertions;
using WorldEcon.Domain.Actions;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit.Actions;

public class DmActionTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void Create_SetsFields()
    {
        var world = WorldId.New();
        var a = DmAction.Create(world, sequence: 7, appliedTick: new Tick(120),
            kind: DmActionKind.AdjustMarketStock, argsJson: "{\"good\":\"x\"}",
            description: "Bumped stock", recordedAtUtc: At).Value;

        a.WorldId.Should().Be(world);
        a.Sequence.Should().Be(7);
        a.AppliedTick.Should().Be(new Tick(120));
        a.Kind.Should().Be(DmActionKind.AdjustMarketStock);
        a.ArgsJson.Should().Be("{\"good\":\"x\"}");
        a.Description.Should().Be("Bumped stock");
        a.RecordedAtUtc.Should().Be(At);
    }

    [Test]
    public void Create_AllowsEmptyArgsJson()
        => DmAction.Create(WorldId.New(), 0, Tick.Zero, DmActionKind.BuyFromShops, "", "Bought goods", At)
            .IsError.Should().BeFalse();

    [Test]
    public void Create_RejectsBlankDescription()
        => DmAction.Create(WorldId.New(), 0, Tick.Zero, DmActionKind.BuyFromShops, "{}", "  ", At)
            .IsError.Should().BeTrue();

    [Test]
    public void Create_RejectsNegativeSequence()
        => DmAction.Create(WorldId.New(), -1, Tick.Zero, DmActionKind.BuyFromShops, "{}", "Bought goods", At)
            .IsError.Should().BeTrue();
}
