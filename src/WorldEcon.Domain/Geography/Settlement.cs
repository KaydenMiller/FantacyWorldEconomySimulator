using ErrorOr;
using WorldEcon.SharedKernel.Domain;
using WorldEcon.SharedKernel.Globe;

namespace WorldEcon.Domain.Geography;

public sealed class Settlement : AggregateRoot<SettlementId>
{
    public WorldId WorldId { get; }
    public PoliticalRegionId PoliticalRegionId { get; private set; }
    public string Name { get; private set; }
    public SettlementType Type { get; private set; }
    public int X { get; private set; }              // display coordinate only (spec §9.1)
    public int Y { get; private set; }
    public long Population { get; private set; }
    public SettlementState State { get; private set; }
    public Provenance Provenance { get; private set; }
    public GlobeCellId? CellId { get; private set; }   // globe cell this settlement is pinned to; null until pinned
    public int? TileId { get; private set; }           // tile (inside CellId) this settlement is pinned to; null until pinned to a tile

    // Parameterless ctor for EF Core materialization.
    private Settlement() : base(default) => Name = null!;

    private Settlement(SettlementId id, WorldId worldId, PoliticalRegionId regionId, string name,
        SettlementType type, int x, int y, long population, SettlementState state, Provenance provenance) : base(id)
    {
        WorldId = worldId;
        PoliticalRegionId = regionId;
        Name = name;
        Type = type;
        X = x;
        Y = y;
        Population = population;
        State = state;
        Provenance = provenance;
    }

    public static ErrorOr<Settlement> Create(WorldId worldId, PoliticalRegionId regionId, string name,
        SettlementType type, int x, int y, long population)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("settlement.name.blank", "Settlement name must not be blank.");
        if (population < 0)
            return Error.Validation("settlement.population.negative", "Population must not be negative.");

        return new Settlement(SettlementId.New(), worldId, regionId, name.Trim(),
            type, x, y, population, SettlementState.Active, Provenance.Authored);
    }

    public void SetState(SettlementState state) => State = state;

    /// <summary>
    /// Pins the settlement to a globe cell and, when <paramref name="tile"/> is given, to a tile inside it (lookup only; does
    /// not alter terrain). A negative cell id is a <c>settlement.cell.negative</c> error and a negative tile id a
    /// <c>settlement.tile.negative</c> error; either leaves the pin unchanged. The upper bounds and the tile-in-cell rule
    /// depend on the world's grids and are checked by the pinning stage. Pinning without a tile clears an earlier tile.
    /// </summary>
    public ErrorOr<Success> PinTo(GlobeCellId cell, int? tile = null)
    {
        if (cell.Value < 0)
            return Error.Validation("settlement.cell.negative", $"Globe cell id must not be negative (got {cell.Value}).");
        if (tile < 0)
            return Error.Validation("settlement.tile.negative", $"Tile id must not be negative (got {tile}).");
        CellId = cell;
        TileId = tile;
        return Result.Success;
    }
}
