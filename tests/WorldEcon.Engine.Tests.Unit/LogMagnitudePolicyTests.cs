using FluentAssertions;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;

namespace WorldEcon.Engine.Tests.Unit;

public class LogMagnitudePolicyTests
{
    [Test]
    public void DefaultMagnitude_MapsTypes()
    {
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.Trade).Should().Be(LogMagnitude.Routine);
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.MerchantLost).Should().Be(LogMagnitude.Notable);
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.ClaimChanged).Should().Be(LogMagnitude.Major);
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.SettlementRuined).Should().Be(LogMagnitude.Historic);
        LogMagnitudePolicy.DefaultMagnitude(LogEventType.PartyAction).Should().Be(LogMagnitude.Major);
    }

    [Test]
    public void Visible_ByMagnitudeFloor()
    {
        // Historic clears every floor.
        LogMagnitudePolicy.Visible(LogEventType.SettlementRuined, LogMagnitude.Historic, LogScopeKind.Continent).Should().BeTrue();
        // Notable clears Settlement but not Region.
        LogMagnitudePolicy.Visible(LogEventType.MerchantLost, LogMagnitude.Notable, LogScopeKind.Settlement).Should().BeTrue();
        LogMagnitudePolicy.Visible(LogEventType.MerchantLost, LogMagnitude.Notable, LogScopeKind.Region).Should().BeFalse();
    }

    [Test]
    public void Visible_RespectsOverrides()
    {
        // Force: ClaimChanged visible at Country/Continent regardless of magnitude.
        LogMagnitudePolicy.Visible(LogEventType.ClaimChanged, LogMagnitude.Routine, LogScopeKind.Country).Should().BeTrue();
        // Cap: Restock never leaves the shop, even if magnitude were high.
        LogMagnitudePolicy.Visible(LogEventType.Restock, LogMagnitude.Historic, LogScopeKind.Settlement).Should().BeFalse();
    }
}
