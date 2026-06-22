using FluentAssertions;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;

namespace WorldEcon.Domain.Tests.Unit;

public class LogEventScopeTests
{
    [Test]
    public void Create_Succeeds()
    {
        var ev = LogEventId.New();
        var scope = LogEventScope.Create(WorldId.New(), ev, LogScopeKind.Settlement, Guid.NewGuid(), sequence: 7);

        scope.IsError.Should().BeFalse();
        scope.Value.LogEventId.Should().Be(ev);
        scope.Value.ScopeKind.Should().Be(LogScopeKind.Settlement);
        scope.Value.Sequence.Should().Be(7);
    }

    [Test]
    public void Create_WithNegativeSequence_Fails()
    {
        var scope = LogEventScope.Create(WorldId.New(), LogEventId.New(), LogScopeKind.Settlement, Guid.NewGuid(), -1);
        scope.IsError.Should().BeTrue();
        scope.FirstError.Code.Should().Be("logeventscope.sequence.negative");
    }
}
