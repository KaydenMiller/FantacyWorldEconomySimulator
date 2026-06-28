using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;
using WorldEcon.Simulation.Time;

namespace WorldEcon.Tui;

/// <summary>
/// Holds the live <see cref="WorldDbContext"/> and the single <see cref="World"/> the TUI operates
/// on, plus a <see cref="CalendarSystem"/> for formatting in-world dates. The DB is the
/// authoritative state; after mutating actions, callers reload <see cref="World"/> via
/// <see cref="ReloadWorldAsync"/> so the header refreshes.
/// </summary>
public sealed class TuiContext
{
    private TuiContext(WorldDbContext db, World world, string? dbPath)
    {
        Db = db;
        World = world;
        DbPath = dbPath;
        Calendar = new CalendarSystem(world.Calendar);
        DisplayUnits = world.DisplayUnitSystem;
    }

    public WorldDbContext Db { get; }
    public World World { get; private set; }

    /// <summary>The on-disk SQLite path, when known. Required by snapshot actions; null otherwise.</summary>
    public string? DbPath { get; }

    public CalendarSystem Calendar { get; }

    /// <summary>Formats a <see cref="Money"/> value using the world's currency (e.g. "3g 2s 1c").</summary>
    public string FormatMoney(Money money) => World.Currency.Format(money);

    /// <summary>Which unit family the UI presents mass/volume in. Initialised from the world default;
    /// toggled at runtime via <see cref="ToggleUnits"/> (display-only, does not persist).</summary>
    public UnitSystem DisplayUnits { get; private set; }

    public string FormatMass(Mass mass) => MeasurementFormat.FormatMass(mass, DisplayUnits);
    public string FormatVolume(Volume volume) => MeasurementFormat.FormatVolume(volume, DisplayUnits);

    /// <summary>Flip metric ↔ imperial for display.</summary>
    public void ToggleUnits() =>
        DisplayUnits = DisplayUnits == UnitSystem.Metric ? UnitSystem.Imperial : UnitSystem.Metric;

    /// <summary>e.g. "Y1 M1 D11 00:00 (tick 14400)".</summary>
    public string CurrentDateLabel
    {
        get
        {
            var d = Calendar.ToDate(World.CurrentTick);
            return $"Y{d.Year} M{d.Month} D{d.Day} {d.Hour:D2}:{d.Minute:D2} (tick {World.CurrentTick.Value})";
        }
    }

    /// <summary>Loads the single world (first world). Throws if none exists.</summary>
    public static Task<TuiContext> LoadAsync(WorldDbContext db) => LoadAsync(db, null);

    /// <summary>Loads the single world and remembers the on-disk db path (for snapshots).</summary>
    public static async Task<TuiContext> LoadAsync(WorldDbContext db, string? dbPath)
    {
        ArgumentNullException.ThrowIfNull(db);

        var world = await db.Worlds.FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("No world found in the database.");

        return new TuiContext(db, world, dbPath);
    }

    /// <summary>Re-reads the world from the DB so time-dependent state (header) refreshes.</summary>
    public async Task ReloadWorldAsync()
    {
        World = await Db.Worlds.FirstOrDefaultAsync(w => w.Id == World.Id)
            ?? throw new InvalidOperationException("World disappeared from the database.");
    }
}
