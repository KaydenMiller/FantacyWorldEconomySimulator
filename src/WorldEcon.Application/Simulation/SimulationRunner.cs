using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Globe;
using WorldEcon.Application.Queries;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Application.Simulation;

/// <summary>
/// A simulation run on a world file for the desktop app: one <see cref="EngineRun"/> (one transaction) on its own
/// connection, stepped by the caller, with the economy reads served from the same connection so they see the running
/// state. Steps, the commit and reads take turns (one at a time). The file must be in WAL mode
/// (<see cref="WorldFileJournal"/>) so other connections can still read the committed state while the run is open.
/// </summary>
public sealed class SimulationRunner : ISimulationRun
{
    private readonly WorldDbContext _db;
    private readonly EngineRun _run;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _stepping;
    private bool _disposed;

    private SimulationRunner(WorldDbContext db, EngineRun run)
    {
        _db = db;
        _run = run;
    }

    /// <summary>Opens a run with the standard phases (<see cref="StandardPhases.All"/>).</summary>
    public static Task<ErrorOr<SimulationRunner>> OpenAsync(string dbPath, CancellationToken ct = default)
        => OpenAsync(dbPath, StandardPhases.All(), ct);

    /// <summary>
    /// Opens a run with <paramref name="phases"/> (tests). Order: the file exists, it is in WAL mode, then the context is
    /// opened and the run begins (its transaction before the world is loaded).
    /// </summary>
    internal static async Task<ErrorOr<SimulationRunner>> OpenAsync(string dbPath, IEnumerable<ISimulationPhase> phases, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        if (!File.Exists(dbPath)) return Errors.NoWorld;
        if (!await WorldFileJournal.IsWalAsync(dbPath, ct)) return Errors.NotWal;

        var db = GlobeWorldLoader.Open(dbPath);
        try
        {
            var world = await db.Worlds.AsNoTracking().FirstOrDefaultAsync(ct);
            if (world is null)
            {
                await db.DisposeAsync();
                return Errors.NoWorld;
            }

            var run = await new TickEngine(phases).BeginRunAsync(db, world.Id, ct);
            return new SimulationRunner(db, run);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // busy, locked
        {
            await db.DisposeAsync();
            return Errors.Busy;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public long CurrentTick => _run.Context.World.CurrentTick.Value;

    /// <inheritdoc />
    public long NextBoundaryTick => _run.NextBoundaryTick;

    /// <inheritdoc />
    public async Task<ErrorOr<RunStep>> StepToAsync(long tick, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _stepping, 1) == 1)
            throw new InvalidOperationException("A step of this run is already running.");
        try
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (tick <= CurrentTick) return Errors.TargetNotAfterNow;
                var tickProgress = progress is null ? null : new TickProgress(progress);
                var reached = await _run.StepToAsync(new Tick(tick), tickProgress, ct);
                return new RunStep(reached.Value, _run.LastStepRanPhases);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _stepping, 0);
        }
    }

    /// <inheritdoc />
    public Task EndAsync() => UnderGateAsync(_ => _run.EndAsync(), CancellationToken.None);

    /// <inheritdoc />
    public Task<ErrorOr<CaravanPositions>> CaravanPositionsAsync(CancellationToken ct = default)
        => UnderGateAsync(c => CaravanPositionsQuery.RunAsync(_db, c), ct);

    /// <inheritdoc />
    public Task<ErrorOr<SettlementDetails>> SettlementDetailsAsync(Guid settlementId, CancellationToken ct = default)
        => UnderGateAsync(c => SettlementDetailsQuery.RunAsync(_db, settlementId, c), ct);

    /// <inheritdoc />
    public Task<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>> SettlementListAsync(CancellationToken ct = default)
        => UnderGateAsync(c => SettlementDetailsQuery.ListAsync(_db, c), ct);

    /// <summary>Rolls the run back unless it was ended, then closes the connection. Waits for a running step or read.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _run.DisposeAsync();
            await _db.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UnderGateAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await body(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> UnderGateAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await body(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class TickProgress(IProgress<long> inner) : IProgress<Tick>
    {
        public void Report(Tick value) => inner.Report(value.Value);
    }

    /// <summary>Error codes of the runner.</summary>
    public static class Errors
    {
        /// <summary>The file is not in WAL mode.</summary>
        public static readonly Error NotWal = Error.Failure("Runner.NotWal", "The world file is not in WAL mode; reopen it in the app.");

        /// <summary>The file does not exist or holds no world.</summary>
        public static readonly Error NoWorld = Error.NotFound("Runner.NoWorld", "The file holds no world.");

        /// <summary>Another connection is writing to the file.</summary>
        public static readonly Error Busy = Error.Conflict("Runner.Busy", "Another program is writing to the world file; try again.");

        /// <summary>The step's target is not after the current tick.</summary>
        public static readonly Error TargetNotAfterNow = Error.Validation("Runner.TargetNotAfterNow", "The target time must be after the current time.");
    }
}
