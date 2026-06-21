using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.Simulation.Random;
using WorldEcon.Simulation.Time;

namespace WorldEcon.Engine;

/// <summary>
/// The live simulation state handed to every phase within an advance. The SQLite-backed
/// <see cref="WorldDbContext"/> is the authoritative world state (architecture decision):
/// phases load/mutate tracked entities, and the engine saves once per advance.
/// </summary>
public sealed class SimulationContext
{
    private SimulationContext(WorldDbContext db, World world, IRngStreams rng, CalendarSystem calendar)
    {
        Db = db;
        World = world;
        Rng = rng;
        Calendar = calendar;
    }

    public WorldDbContext Db { get; }
    public World World { get; }
    public IRngStreams Rng { get; }
    public CalendarSystem Calendar { get; }

    /// <summary>
    /// Loads the single <see cref="World"/> by id (tracked, so mutations persist on save) and
    /// builds the calendar and RNG streams from it. Throws if the world is not found.
    /// </summary>
    /// <remarks>
    /// RNG-state persistence across advances is deferred; seeding from <see cref="World.Seed"/>
    /// is fine for now since no phase consumes RNG yet.
    /// </remarks>
    public static async Task<SimulationContext> LoadAsync(WorldDbContext db, WorldId worldId)
    {
        ArgumentNullException.ThrowIfNull(db);

        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId)
            ?? throw new InvalidOperationException($"World '{worldId.Value}' not found.");

        var calendar = new CalendarSystem(world.Calendar);
        var rng = new RngStreams(world.Seed);

        return new SimulationContext(db, world, rng, calendar);
    }
}
