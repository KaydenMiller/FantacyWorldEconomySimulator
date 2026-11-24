using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tectonics;

/// <summary>
/// Which plates claim each cell after a move (<see cref="PlateFrames.Claim"/>): <see cref="Count"/> claimants (0 = a gap,
/// 2 or more = an overlap; only the two lowest plate ids are kept), their plate ids and the frame cell each looked up.
/// </summary>
public sealed class ClaimResult(int cells)
{
    public int[] Count { get; } = new int[cells];
    public int[] PlateA { get; } = new int[cells];
    public int[] FrameA { get; } = new int[cells];
    public int[] PlateB { get; } = new int[cells];
    public int[] FrameB { get; } = new int[cells];
}

/// <summary>
/// Plate motion on the fixed globe grid (design 2026-11-24, section 5.1). Each plate keeps its crust in its own
/// reference frame: a map from frame cell to crust slot (−1 = no crust), taken when the plate was last rebased, plus an
/// accumulated <see cref="QuantisedRotation"/>. A grid cell belongs to plate p when its centre, rotated back by p's
/// rotation, lands (nearest cell) on a frame cell holding p's crust. One plate therefore never claims a cell twice and
/// never leaves a hole inside itself; plates move any distance per step; every lookup is a gather, so parallel and serial
/// claims are equal. Plates are rebased (frame = present positions, angle 0) at rift and fusion events.
/// </summary>
public sealed class PlateFrames
{
    private readonly GlobeGeometry _geo;
    private readonly TileGrid _lattice;
    private readonly int _cells;
    private readonly double _cellAngle;
    private readonly List<int[]> _slot = [];
    private readonly List<QuantisedRotation> _rotation = [];
    private readonly List<(Vector3d Centre, double Cos)> _cap = [];

    /// <summary>Frames from present plate membership; the initial slot of each cell's crust is its cell id.</summary>
    public PlateFrames(GlobeGrid grid, GlobeGeometry geo, ReadOnlySpan<int> plateOf)
    {
        ArgumentNullException.ThrowIfNull(grid);
        _geo = geo ?? throw new ArgumentNullException(nameof(geo));
        _lattice = new TileGrid(grid.Frequency);
        _cells = grid.CellCount;
        _cellAngle = Math.Sqrt(4 * Math.PI / _cells);
        var plates = 0;
        foreach (var p in plateOf) plates = Math.Max(plates, p + 1);
        for (var p = 0; p < plates; p++)
        {
            var slots = new int[_cells];
            for (var c = 0; c < _cells; c++) slots[c] = plateOf[c] == p ? c : -1;
            _slot.Add(slots);
            _rotation.Add(new QuantisedRotation(new Vector3d(0, 0, 1), 0));
            _cap.Add(Cap(slots));
        }
    }

    public int PlateCount => _slot.Count;

    public QuantisedRotation Rotation(int plate) => _rotation[plate];

    public void SetAxis(int plate, Vector3d unitAxis) => _rotation[plate] = _rotation[plate] with { Axis = unitAxis };

    public void Advance(int plate, long deltaMicroRad) => _rotation[plate] = _rotation[plate].Advance(deltaMicroRad);

    /// <summary>Crust slot at <paramref name="frameCell"/> of <paramref name="plate"/>'s frame, −1 for none.</summary>
    public int SlotOf(int plate, int frameCell) => _slot[plate][frameCell];

    /// <summary>The frame cell of <paramref name="plate"/> under present cell <paramref name="cell"/>.</summary>
    public int FrameCellOf(int plate, int cell) => _lattice.Nearest(_rotation[plate].ApplyInverse(_geo.Centre(cell)));

    /// <summary>Puts crust slot <paramref name="slot"/> (−1 removes) at the frame cell under present cell <paramref name="cell"/>.</summary>
    /// <remarks>Does not update the plate's bounding cap: call <see cref="RefreshCaps"/> after a batch of additions.</remarks>
    public void SetSlotAt(int plate, int cell, int slot) => _slot[plate][FrameCellOf(plate, cell)] = slot;

    /// <summary>Recomputes every plate's bounding cap (after crust was added with <see cref="SetSlotAt"/>).</summary>
    public void RefreshCaps()
    {
        for (var p = 0; p < _slot.Count; p++) _cap[p] = Cap(_slot[p]);
    }

    /// <summary>Adds an empty plate (rift); returns its id.</summary>
    public int AddPlate(Vector3d unitAxis)
    {
        var slots = new int[_cells];
        Array.Fill(slots, -1);
        _slot.Add(slots);
        _rotation.Add(new QuantisedRotation(unitAxis, 0));
        _cap.Add(Cap(slots));
        return _slot.Count - 1;
    }

    /// <summary>Removes plates whose <paramref name="newId"/> is −1 and renumbers the rest (ids must stay in order).</summary>
    public void Renumber(ReadOnlySpan<int> newId)
    {
        var slots = new List<int[]>();
        var rot = new List<QuantisedRotation>();
        var cap = new List<(Vector3d, double)>();
        for (var p = 0; p < _slot.Count; p++)
        {
            if (newId[p] < 0) continue;
            slots.Add(_slot[p]);
            rot.Add(_rotation[p]);
            cap.Add(_cap[p]);
        }

        _slot.Clear(); _slot.AddRange(slots);
        _rotation.Clear(); _rotation.AddRange(rot);
        _cap.Clear(); _cap.AddRange(cap);
    }

    /// <summary>Rebases <paramref name="plate"/> to its present cells, keeping the crust each one carries now.</summary>
    public void Rebase(int plate, ReadOnlySpan<int> plateOfCell)
    {
        var slots = new int[_cells];
        for (var c = 0; c < _cells; c++) slots[c] = plateOfCell[c] == plate ? _slot[plate][FrameCellOf(plate, c)] : -1;
        Install(plate, slots);
    }

    /// <summary>Rebases <paramref name="plate"/> to the present cells assigned to it with the given crust slots (fusion, rift).</summary>
    public void Rebase(int plate, ReadOnlySpan<int> plateOfCell, ReadOnlySpan<int> slotOfCell)
    {
        var slots = new int[_cells];
        for (var c = 0; c < _cells; c++) slots[c] = plateOfCell[c] == plate ? slotOfCell[c] : -1;
        Install(plate, slots);
    }

    /// <summary>Which plates claim each cell at their present rotations.</summary>
    public ClaimResult Claim(ParallelOptions? options = null)
    {
        var result = new ClaimResult(_cells);
        var n = _slot.Count;
        var inverse = new Rotation3[n];
        var caps = new (Vector3d Centre, double Cos)[n];
        for (var p = 0; p < n; p++)
        {
            var m = _rotation[p].Matrix();
            inverse[p] = m;
            caps[p] = (m.Apply(_cap[p].Centre), _cap[p].Cos);
        }

        Parallel.For(0, (_cells + 1023) / 1024, options ?? new ParallelOptions(), chunk =>
        {
            var end = Math.Min(_cells, (chunk + 1) * 1024);
            for (var c = chunk * 1024; c < end; c++)
            {
                var centre = _geo.Centre(c);
                var count = 0;
                for (var p = 0; p < n; p++)
                {
                    if (Vector3d.Dot(centre, caps[p].Centre) < caps[p].Cos) continue;
                    var f = _lattice.Nearest(inverse[p].ApplyTransposed(centre));
                    if (_slot[p][f] < 0) continue;
                    if (count == 0) { result.PlateA[c] = p; result.FrameA[c] = f; }
                    else if (count == 1) { result.PlateB[c] = p; result.FrameB[c] = f; }
                    count++;
                }

                result.Count[c] = count;
            }
        });
        return result;
    }

    private void Install(int plate, int[] slots)
    {
        _slot[plate] = slots;
        _rotation[plate] = _rotation[plate] with { AngleMicroRad = 0 };
        _cap[plate] = Cap(slots);
    }

    // Bounding cap of a frame (centre and cosine of its angular radius plus two cell spacings of margin), for skipping
    // plates far from a cell. An empty frame gets a cap that never matches.
    private (Vector3d Centre, double Cos) Cap(int[] slots)
    {
        double x = 0, y = 0, z = 0;
        var any = false;
        for (var c = 0; c < slots.Length; c++)
        {
            if (slots[c] < 0) continue;
            var v = _geo.Centre(c);
            x += v.X; y += v.Y; z += v.Z;
            any = true;
        }

        if (!any) return (new Vector3d(0, 0, 1), 2);
        var len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-9) return (new Vector3d(0, 0, 1), -2); // spread over the whole globe: always test
        var centre = new Vector3d(x / len, y / len, z / len);
        var minDot = 1.0;
        for (var c = 0; c < slots.Length; c++)
            if (slots[c] >= 0) minDot = Math.Min(minDot, Vector3d.Dot(centre, _geo.Centre(c)));
        var radius = Math.Acos(Math.Clamp(minDot, -1, 1)) + 2 * _cellAngle;
        return (centre, radius >= Math.PI ? -2 : Math.Cos(radius));
    }
}
