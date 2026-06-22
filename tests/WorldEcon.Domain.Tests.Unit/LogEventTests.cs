using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit;

public class LogEventTests
{
    private static readonly WorldId World = WorldId.New();

    [Test]
    public void Create_WithValidArgs_Succeeds()
    {
        var result = LogEvent.Create(World, sequence: 0, new Tick(10), LogEventType.Trade,
            LogMagnitude.Routine, LogScopeKind.Shop, Guid.NewGuid(), isPlayerAction: false,
            payloadJson: "{}", message: "Sold 3 potions");

        result.IsError.Should().BeFalse();
        result.Value.Message.Should().Be("Sold 3 potions");
        result.Value.Sequence.Should().Be(0);
        result.Value.IsPlayerAction.Should().BeFalse();
    }

    [Test]
    public void Create_WithNegativeSequence_Fails()
    {
        var result = LogEvent.Create(World, sequence: -1, new Tick(10), LogEventType.Trade,
            LogMagnitude.Routine, LogScopeKind.Shop, Guid.NewGuid(), false, "{}", "x");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("logevent.sequence.negative");
    }

    [Test]
    public void Create_WithBlankMessage_Fails()
    {
        var result = LogEvent.Create(World, 0, new Tick(10), LogEventType.Trade,
            LogMagnitude.Routine, LogScopeKind.Shop, Guid.NewGuid(), false, "{}", "  ");

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("logevent.message.blank");
    }
}
