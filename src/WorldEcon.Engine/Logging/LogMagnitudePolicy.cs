using WorldEcon.Domain.Logging;

namespace WorldEcon.Engine.Logging;

/// <summary>Pure rules for how events surface: default magnitude per type, the visibility floor per
/// level, and per-type overrides. Tunable here now; promote to world/config params later.</summary>
public static class LogMagnitudePolicy
{
    public static LogMagnitude DefaultMagnitude(LogEventType type) => type switch
    {
        LogEventType.Trade or LogEventType.Restock or LogEventType.Spoilage => LogMagnitude.Routine,
        LogEventType.MerchantArrived or LogEventType.MerchantDeparted
            or LogEventType.MerchantGained or LogEventType.MerchantLost
            or LogEventType.ProductionChanged or LogEventType.Stockout => LogMagnitude.Notable,
        LogEventType.RouteOpened or LogEventType.RouteClosed or LogEventType.ClaimChanged => LogMagnitude.Major,
        LogEventType.SettlementFounded or LogEventType.SettlementRuined => LogMagnitude.Historic,
        LogEventType.PartyAction => LogMagnitude.Major,
        _ => LogMagnitude.Notable,
    };

    /// <summary>Lowest magnitude that surfaces at a given level (before per-type overrides).</summary>
    public static LogMagnitude FloorFor(LogScopeKind level) => level switch
    {
        LogScopeKind.Shop or LogScopeKind.Factory or LogScopeKind.Merchant => LogMagnitude.Routine,
        LogScopeKind.Settlement => LogMagnitude.Notable,
        LogScopeKind.Region or LogScopeKind.Country => LogMagnitude.Major,
        LogScopeKind.Continent or LogScopeKind.World => LogMagnitude.Historic,
        _ => LogMagnitude.Historic,
    };

    /// <summary>Whether an event of (type, magnitude) surfaces at <paramref name="level"/>, applying
    /// per-type force/cap overrides. The ORIGIN scope is always written by the emitter regardless of
    /// this method.</summary>
    public static bool Visible(LogEventType type, LogMagnitude magnitude, LogScopeKind level)
    {
        // Cap override: a restock never leaves the originating shop.
        if (type == LogEventType.Restock && level != LogScopeKind.Shop)
            return false;

        // Force override: claim changes are always visible at country and continent (regional politics,
        // not world-historic). World scope is only reached if the event's magnitude clears the World floor.
        if (type == LogEventType.ClaimChanged && (level == LogScopeKind.Country || level == LogScopeKind.Continent))
            return true;

        return (int)magnitude >= (int)FloorFor(level);
    }
}
