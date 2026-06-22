using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Logging;

/// <summary>Deletes engine-generated events past their magnitude-tier age. Player actions and
/// Major/Historic events are never pruned. The cutoff is age-relative-to-now and deletions are
/// monotonic, so pruning is granularity-independent (advance(N) == k×advance(N/k) surviving set).</summary>
public static class LogRetention
{
    // Tunable now; promote to world/config params later.
    public static readonly long RoutineMaxAgeTicks = 90 * Tick.DefaultMinutesPerDay;    // 90 in-world days
    public static readonly long NotableMaxAgeTicks = 5 * 360 * Tick.DefaultMinutesPerDay; // 5 in-world years (360-day year)

    /// <summary>Marks prunable events (and their scope rows) for deletion on the tracked context.
    /// The caller's SaveChanges persists the removal.</summary>
    public static async Task PruneAsync(WorldDbContext db, WorldId worldId, Tick now)
    {
        long routineCutoff = now.Value - RoutineMaxAgeTicks;
        long notableCutoff = now.Value - NotableMaxAgeTicks;

        // WorldId equality + bool translate in SQL; the tick range check is value-converted, so do it
        // in memory after materializing the (bounded, retention-capped) candidate set.
        var candidates = await db.LogEvents
            .Where(e => e.WorldId == worldId && !e.IsPlayerAction)
            .ToListAsync();

        var prunable = candidates.Where(e => e.Magnitude switch
        {
            LogMagnitude.Routine => e.OccurredTick.Value < routineCutoff,
            LogMagnitude.Notable => e.OccurredTick.Value < notableCutoff,
            _ => false, // Major / Historic never pruned
        }).ToList();

        if (prunable.Count == 0)
            return;

        var prunableSeqs = prunable.Select(e => e.Sequence).ToHashSet();
        var scopes = await db.LogEventScopes
            .Where(x => x.WorldId == worldId && prunableSeqs.Contains(x.Sequence))
            .ToListAsync();

        db.LogEventScopes.RemoveRange(scopes);
        db.LogEvents.RemoveRange(prunable);
    }
}
