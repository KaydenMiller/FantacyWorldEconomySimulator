using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Tectonics;

/// <summary>
/// The running state of the plate history simulation (design 2026-11-24, section 5): the crust slot pool, plate frames
/// and motions, and the present plate and crust slot of every cell. <see cref="StepMotionAndInteractions"/> runs steps 1–3
/// of section 5 (move, resolve overlaps, fill gaps, uplift and collision footprints); events (<see cref="Events"/>) and
/// surface processes (<see cref="Surface"/>) act on the same state.
/// </summary>
/// <summary>A rift scheduled to fail: plates <paramref name="A"/> and <paramref name="B"/> fuse again at step <paramref name="AtStep"/>.</summary>
public sealed record ScheduledRefusion(int A, int B, int AtStep, IReadOnlyList<int> LineSlots)
{
    public int A { get; set; } = A;
    public int B { get; set; } = B;
}

public sealed class StepState
{
    /// <summary>Crust thickening per step at the peak of the subduction uplift profile, dam (600 m of crust ≈ 90 m of height).</summary>
    public const int PeakAndeanUpliftDam = 60;

    /// <summary>Thickening per step at the peak of an island-arc profile, dam.</summary>
    public const int PeakArcUpliftDam = 40;

    /// <summary>Thickness above which uplifted oceanic crust becomes accreted arc crust, dam (15 km).</summary>
    public const int ArcAccretionDam = 1_500;

    /// <summary>
    /// Overlapping continental cells two plates need in one step to count as a collision contact (fusion clock). Single
    /// overlaps come from nearest-cell resampling along any boundary, including diverging ones.
    /// </summary>
    public const int MinCollisionOverlapCells = 3;

    /// <summary>Thickness cap for any crust, dam (80 km, about Tibet's crust).</summary>
    public const int MaxThicknessDam = 8_000;

    private readonly Dictionary<(int A, int B), int> _contactSteps = [];
    private readonly Dictionary<(int A, int B), int> _contactCell = [];

    private StepState(GlobeGrid grid, GlobeGeometry geo, WorldGenSettings settings, CrustRecord pool, PlateFrames frames,
        List<Vector3d> axis, List<long> stepMicroRad, int[] plateOf, int[] slotOf, ulong seed)
    {
        Grid = grid;
        Geo = geo;
        Settings = settings;
        Pool = pool;
        Frames = frames;
        Axis = axis;
        StepMicroRad = stepMicroRad;
        PlateOfCell = plateOf;
        SlotOfCell = slotOf;
        Seed = seed;
        CellAngle = Math.Sqrt(4 * Math.PI / grid.CellCount);
    }

    public GlobeGrid Grid { get; }
    public GlobeGeometry Geo { get; }
    public WorldGenSettings Settings { get; }
    public TectonicsSettings T => Settings.Tectonics;
    public CrustRecord Pool { get; }
    public PlateFrames Frames { get; }

    /// <summary>Unit rotation axis of each plate.</summary>
    public List<Vector3d> Axis { get; }

    /// <summary>Rotation per step of each plate, micro-radians (may be negative).</summary>
    public List<long> StepMicroRad { get; }

    /// <summary>Each plate's own speed before slab pull and drag (micro-radians per step, magnitude); <see cref="PlateDynamics"/> eases <see cref="StepMicroRad"/> toward a target derived from it.</summary>
    public List<long> BaseStepMicroRad { get; } = [];

    /// <summary>Last step: cells each plate lost by subducting under a neighbour.</summary>
    public int[] LastSubductingCells { get; private set; } = [];

    /// <summary>Last step: cells of each plate with a neighbour on another plate.</summary>
    public int[] LastBoundaryCells { get; private set; } = [];

    /// <summary>Last step: plates in a continental collision.</summary>
    public HashSet<int> LastCollided { get; } = [];

    public int[] PlateOfCell { get; set; }
    public int[] SlotOfCell { get; set; }
    public int StepIndex { get; set; }
    public ulong Seed { get; }

    /// <summary>Mean angular spacing of cells, radians.</summary>
    public double CellAngle { get; }

    public int PlateCount => Axis.Count;

    /// <summary>Cells of a reference plate for the rift probability (A0 in Cortial et al.): cells / starting plate count.</summary>
    public double ReferencePlateCells { get; init; } = 1;

    /// <summary>Rifts scheduled to fail: the two plates fuse again at <c>AtStep</c>, and the rift-line crust slots get <see cref="CrustFlags.FailedRift"/>.</summary>
    public List<ScheduledRefusion> Scheduled { get; } = [];

    /// <summary>
    /// A state from present plates and a per-cell record (slot = cell). Plate speeds are mm/year at the fastest point
    /// (negative reverses the rotation).
    /// </summary>
    public static StepState Create(GlobeGrid grid, GlobeGeometry geo, WorldGenSettings settings, int[] plateOf, CrustRecord perCell,
        IReadOnlyList<Vector3d> axes, IReadOnlyList<double> speedsMmPerYr, ulong seed)
    {
        var frames = new PlateFrames(grid, geo, plateOf);
        var axis = new List<Vector3d>();
        var step = new List<long>();
        for (var p = 0; p < axes.Count; p++)
        {
            axis.Add(axes[p]);
            step.Add(QuantisedRotation.StepMicroRad(speedsMmPerYr[p], settings.Tectonics.StepMy, settings.Grid.PlanetRadiusKm));
            if (p < frames.PlateCount) frames.SetAxis(p, axes[p]);
        }

        var slots = Enumerable.Range(0, grid.CellCount).ToArray();
        return new StepState(grid, geo, settings, perCell, frames, axis, step, (int[])plateOf.Clone(), slots, seed)
        {
            ReferencePlateCells = grid.CellCount / (double)Math.Max(1, axes.Count),
        };
    }

    /// <summary>Angular velocity of a plate, radians per step, as a vector along its axis.</summary>
    public Vector3d Omega(int plate) => Axis[plate] * (StepMicroRad[plate] / 1e6);

    /// <summary>Velocity of plate <paramref name="plate"/> at cell <paramref name="cell"/>, radians per step on the unit sphere.</summary>
    public Vector3d VelocityAt(int plate, int cell) => Vector3d.Cross(Omega(plate), Geo.Centre(cell));

    /// <summary>Fastest plate speed, radians per step.</summary>
    public double MaxSpeed => StepMicroRad.Count == 0 ? 0 : StepMicroRad.Max(s => Math.Abs(s)) / 1e6;

    /// <summary>The per-cell record now.</summary>
    public CrustRecord Present() => Pool.Gather(SlotOfCell);

    /// <summary>Collision footprint radius in cells: factor × sqrt(terrane cells × closing speed in cells per step), at least 1.</summary>
    public static double CollisionRadiusCells(TectonicsSettings t, int terraneCells, double closingCellsPerStep) =>
        Math.Max(1, t.CollisionRadiusFactorBp / 10_000.0 * Math.Sqrt(Math.Max(0, terraneCells) * Math.Max(0, closingCellsPerStep)) / 4);

    /// <summary>Steps 1–3 of a simulation step: rotate plates, claim cells, resolve overlaps, fill gaps, apply uplift.</summary>
    public void StepMotionAndInteractions()
    {
        for (var p = 0; p < PlateCount; p++) Frames.Advance(p, StepMicroRad[p]);
        var claims = Frames.Claim();
        var n = Grid.CellCount;
        var newPlate = new int[n];
        var newSlot = new int[n];
        var andeanFront = new List<(int Cell, int Plate)>();
        var arcFront = new List<(int Cell, int Plate)>();
        var collisions = new List<(int Cell, int Survivor, int Other, int AddedDam)>();
        var contactThisStep = new HashSet<(int, int)>();
        var overlapCount = new Dictionary<(int, int), int>();
        var firstContactCell = new Dictionary<(int, int), int>();
        var r = Pool;
        // Slots are read before any crust is removed: two cells can look up the same frame cell (nearest-cell resampling).
        var slotA = new int[n];
        var slotB = new int[n];
        for (var c = 0; c < n; c++)
        {
            if (claims.Count[c] >= 1) slotA[c] = Frames.SlotOf(claims.PlateA[c], claims.FrameA[c]);
            if (claims.Count[c] >= 2) slotB[c] = Frames.SlotOf(claims.PlateB[c], claims.FrameB[c]);
        }

        var removals = new List<(int Plate, int Cell)>();
        var subducting = new int[PlateCount];
        LastCollided.Clear();

        for (var c = 0; c < n; c++)
        {
            switch (claims.Count[c])
            {
                case 0:
                {
                    var plate = PlateOfCell[c];
                    var slot = r.Allocate();
                    var beside = false;
                    foreach (var nb in Grid.Neighbours(c))
                    {
                        var ns = SlotOfCell[nb];
                        if (r.Kind[ns] == CrustKind.Continental && r.RiftAgeMy[ns] <= 30) beside = true;
                    }

                    r.GapFill(slot, beside);
                    Frames.SetSlotAt(plate, c, slot);
                    newPlate[c] = plate;
                    newSlot[c] = slot;
                    break;
                }
                case 1:
                    newPlate[c] = claims.PlateA[c];
                    newSlot[c] = slotA[c];
                    break;
                default:
                {
                    int a = claims.PlateA[c], b = claims.PlateB[c];
                    int sa = slotA[c], sb = slotB[c];
                    var contA = r.Kind[sa] != CrustKind.Oceanic;
                    var contB = r.Kind[sb] != CrustKind.Oceanic;
                    int survivor, lost, sSurv, sLost;
                    if (contA && contB)
                    {
                        (survivor, lost, sSurv, sLost) = r.ThicknessDam[sb] > r.ThicknessDam[sa] ? (b, a, sb, sa) : (a, b, sa, sb);
                        var added = r.ThicknessDam[sLost];
                        r.ThicknessDam[sSurv] = (ushort)Math.Min(MaxThicknessDam, r.ThicknessDam[sSurv] + added);
                        collisions.Add((c, survivor, lost, added));
                        LastCollided.Add(a);
                        LastCollided.Add(b);
                        var key = (Math.Min(a, b), Math.Max(a, b));
                        overlapCount[key] = overlapCount.GetValueOrDefault(key) + 1;
                        firstContactCell.TryAdd(key, c);
                    }
                    else if (contA != contB)
                    {
                        (survivor, lost, sSurv, sLost) = contA ? (a, b, sa, sb) : (b, a, sb, sa);
                        andeanFront.Add((c, survivor));
                        subducting[lost]++;
                    }
                    else
                    {
                        // Ocean against ocean: the older (larger formed age) subducts; ties: plate B subducts.
                        (survivor, lost, sSurv, sLost) = r.FormedAgeMy[sb] > r.FormedAgeMy[sa] ? (a, b, sa, sb) : (b, a, sb, sa);
                        arcFront.Add((c, survivor));
                        subducting[lost]++;
                    }

                    removals.Add((lost, c));
                    newPlate[c] = survivor;
                    newSlot[c] = sSurv;
                    break;
                }
            }
        }

        foreach (var (plate, cell) in removals) Frames.SetSlotAt(plate, cell, -1);
        PlateOfCell = newPlate;
        SlotOfCell = newSlot;
        Frames.RefreshCaps();
        LastSubductingCells = subducting;
        var boundary = new int[PlateCount];
        for (var c = 0; c < n; c++)
            foreach (var nb in Grid.Neighbours(c))
                if (PlateOfCell[nb] != PlateOfCell[c]) { boundary[PlateOfCell[c]]++; break; }
        LastBoundaryCells = boundary;

        // A contact needs several overlapping cells and the two plates approaching each other: interlocked boundaries of
        // plates moving apart (a fresh rift) also overlap where tongues of one side lie ahead of the other.
        foreach (var (key, count) in overlapCount)
        {
            if (count < MinCollisionOverlapCells) continue;
            var cell = firstContactCell[key];
            // Local approach: from plate A's cells around the contact toward plate B's (plate centres mislead for large plates).
            Vector3d sumA = default, sumB = default;
            foreach (var nb in Grid.Neighbours(cell))
            {
                if (PlateOfCell[nb] == key.Item1) sumA = sumA + Geo.Centre(nb);
                else if (PlateOfCell[nb] == key.Item2) sumB = sumB + Geo.Centre(nb);
            }

            if (sumA.Length > 0 && sumB.Length > 0)
            {
                var closing = Vector3d.Dot(VelocityAt(key.Item1, cell) - VelocityAt(key.Item2, cell), sumB.Normalize() - sumA.Normalize());
                if (closing <= 0) continue;
            }
            contactThisStep.Add(key);
            _contactCell.TryAdd(key, cell);
        }

        var touched = new HashSet<int>();
        ApplyProfile(andeanFront, PeakAndeanUpliftDam, OrogenyKind.Andean, touched);
        ApplyProfile(arcFront, PeakArcUpliftDam, OrogenyKind.Arc, touched);
        SpreadCollisions(collisions);

        foreach (var key in _contactSteps.Keys.ToList())
            if (!contactThisStep.Contains(key)) { _contactSteps.Remove(key); _contactCell.Remove(key); }
        foreach (var key in contactThisStep) _contactSteps[key] = _contactSteps.GetValueOrDefault(key) + 1;
        StepIndex++;
    }

    /// <summary>Plate pairs in continental contact that should fuse: slow relative motion, or contact for <see cref="TectonicsSettings.CollisionMaxSteps"/> steps.</summary>
    public IReadOnlyList<(int A, int B)> FusionCandidates()
    {
        var result = new List<(int, int)>();
        var limit = T.FusionMaxSpeedPermille / 1000.0 * MaxSpeed;
        foreach (var (key, steps) in _contactSteps.OrderBy(kv => kv.Key))
        {
            var cell = _contactCell[key];
            var relative = (VelocityAt(key.A, cell) - VelocityAt(key.B, cell)).Length;
            if (steps >= T.CollisionMaxSteps || relative <= limit) result.Add(key);
        }

        return result;
    }

    /// <summary>Consecutive steps plates <paramref name="a"/> and <paramref name="b"/> have been in continental contact (0 when not).</summary>
    public int ContactSteps(int a, int b) => _contactSteps.GetValueOrDefault((Math.Min(a, b), Math.Max(a, b)));

    /// <summary>
    /// Renumbers the contact history after plate ids change (<paramref name="newId"/>[old] = new id, −1 removed); pairs
    /// containing a removed plate or one of <paramref name="drop"/> are forgotten, every other pair keeps its count.
    /// </summary>
    public void RemapContacts(ReadOnlySpan<int> newId, params int[] drop)
    {
        var steps = new Dictionary<(int, int), int>();
        var cells = new Dictionary<(int, int), int>();
        foreach (var (key, count) in _contactSteps)
        {
            if (drop.Contains(key.A) || drop.Contains(key.B)) continue;
            int a = key.A < newId.Length ? newId[key.A] : -1, b = key.B < newId.Length ? newId[key.B] : -1;
            if (a < 0 || b < 0 || a == b) continue;
            var k = (Math.Min(a, b), Math.Max(a, b));
            steps[k] = count;
            cells[k] = _contactCell[key];
        }

        _contactSteps.Clear();
        _contactCell.Clear();
        foreach (var kv in steps) _contactSteps[kv.Key] = kv.Value;
        foreach (var kv in cells) _contactCell[kv.Key] = kv.Value;
    }

    /// <summary>Forgets contact history of plates (after they fuse or are renumbered).</summary>
    public void ClearContacts()
    {
        _contactSteps.Clear();
        _contactCell.Clear();
    }

    /// <summary>Mean centre of each plate's cells (not normalised; only directions between plates are used).</summary>
    public Vector3d[] PlateCentroids()
    {
        var sum = new Vector3d[PlateCount];
        for (var c = 0; c < Grid.CellCount; c++) sum[PlateOfCell[c]] = sum[PlateOfCell[c]] + Geo.Centre(c);
        return sum.Select(v => v.Length > 1e-12 ? v.Normalize() : v).ToArray();
    }

    /// <summary>Steps from <paramref name="cell"/> to the nearest cell of <paramref name="plate"/> (0 when on it); −1 when unreachable.</summary>
    public int DistanceToPlate(int cell, int plate)
    {
        var dist = new Dictionary<int, int> { [cell] = 0 };
        var q = new Queue<int>([cell]);
        while (q.TryDequeue(out var x))
        {
            if (PlateOfCell[x] == plate) return dist[x];
            foreach (var nb in Grid.Neighbours(x))
                if (dist.TryAdd(nb, dist[x] + 1)) q.Enqueue(nb);
        }

        return -1;
    }

    /// <summary>Sum of continental crust thickness over cells, dam (cells are close to equal in area).</summary>
    public double ContinentalVolumeDam()
    {
        double v = 0;
        for (var c = 0; c < Grid.CellCount; c++)
        {
            var s = SlotOfCell[c];
            if (Pool.Kind[s] != CrustKind.Oceanic) v += Pool.ThicknessDam[s];
        }

        return v;
    }

    // Uplift inboard of fronts: multi-source distance within the overriding plate; f(d) rises to 1 at d = U and falls to
    // 0 at d = 2U. A slot is uplifted once per step (cells sharing a slot after resampling are not counted twice).
    private void ApplyProfile(List<(int Cell, int Plate)> fronts, int peakDam, OrogenyKind kind, HashSet<int> touched)
    {
        if (fronts.Count == 0) return;
        var u = T.SubductionUpliftCells;
        var dist = new Dictionary<int, int>();
        var q = new Queue<int>();
        foreach (var (cell, _) in fronts.OrderBy(f => f.Cell))
            if (dist.TryAdd(cell, 0)) q.Enqueue(cell);
        while (q.TryDequeue(out var x))
        {
            var d = dist[x];
            if (d >= 2 * u) continue;
            foreach (var nb in Grid.Neighbours(x))
                if (PlateOfCell[nb] == PlateOfCell[x] && dist.TryAdd(nb, d + 1)) q.Enqueue(nb);
        }

        foreach (var (cell, d) in dist.OrderBy(kv => kv.Key))
        {
            var f = d <= u ? (d + 1.0) / (u + 1.0) : Math.Max(0, (2.0 * u - d) / u);
            if (f <= 0) continue;
            var s = SlotOfCell[cell];
            if (!touched.Add(s)) continue;
            var r = Pool;
            if (kind == OrogenyKind.Andean && r.Kind[s] == CrustKind.Oceanic) continue;
            r.ThicknessDam[s] = (ushort)Math.Min(MaxThicknessDam, r.ThicknessDam[s] + (int)Math.Round(peakDam * f));
            MarkOrogeny(s, kind);
            r.ArcMagmaAgeMy[s] = 0;
            if (kind == OrogenyKind.Arc && r.Kind[s] == CrustKind.Oceanic && r.ThicknessDam[s] >= ArcAccretionDam) r.Kind[s] = CrustKind.AccretedArc;
        }
    }

    // Spreads the thickness added at each collision cell over the survivor's continental cells within the collision
    // radius (volume conserved), and marks them Himalayan with collision granite.
    private void SpreadCollisions(List<(int Cell, int Survivor, int Other, int AddedDam)> collisions)
    {
        if (collisions.Count == 0) return;
        var continentalCells = new int[PlateCount];
        for (var c = 0; c < Grid.CellCount; c++)
            if (Pool.Kind[SlotOfCell[c]] != CrustKind.Oceanic) continentalCells[PlateOfCell[c]]++;
        var r = Pool;
        foreach (var (cell, survivor, other, added) in collisions)
        {
            var terrane = Math.Min(continentalCells[survivor], continentalCells[other]);
            var closing = (VelocityAt(survivor, cell) - VelocityAt(other, cell)).Length / CellAngle;
            var radius = (int)Math.Round(CollisionRadiusCells(T, terrane, closing));
            var region = new List<int>();
            var seen = new HashSet<int> { cell };
            var q = new Queue<(int Cell, int D)>([(cell, 0)]);
            while (q.TryDequeue(out var item))
            {
                if (r.Kind[SlotOfCell[item.Cell]] != CrustKind.Oceanic) region.Add(item.Cell);
                if (item.D >= radius) continue;
                foreach (var nb in Grid.Neighbours(item.Cell))
                    if (PlateOfCell[nb] == survivor && seen.Add(nb)) q.Enqueue((nb, item.D + 1));
            }

            var s0 = SlotOfCell[cell];
            var share = region.Count > 0 ? added / region.Count : 0;
            r.ThicknessDam[s0] = (ushort)Math.Max(CrustRecord.OceanicThicknessDam, r.ThicknessDam[s0] - share * (region.Count - 1));
            foreach (var x in region.Distinct().OrderBy(x => x))
            {
                var s = SlotOfCell[x];
                if (x != cell) r.ThicknessDam[s] = (ushort)Math.Min(MaxThicknessDam, r.ThicknessDam[s] + share);
                MarkOrogeny(s, OrogenyKind.Himalayan);
                r.Flags[s] |= CrustFlags.CollisionGranite;
            }
        }
    }

    private void MarkOrogeny(int slot, OrogenyKind kind)
    {
        var r = Pool;
        if (r.LastOrogenyAgeMy[slot] == CrustRecord.Never || r.LastOrogenyAgeMy[slot] > 20)
            r.OrogenyCount[slot] = (byte)Math.Min(255, r.OrogenyCount[slot] + 1);
        r.LastOrogenyAgeMy[slot] = 0;
        if (kind == OrogenyKind.Himalayan || r.Orogeny[slot] != OrogenyKind.Himalayan) r.Orogeny[slot] = kind;
    }
}
