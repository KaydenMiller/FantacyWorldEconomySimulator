using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tectonics;

/// <summary>
/// Plate events (design section 5, items 2 and 4): fusion of two plates, rifting with Cortial et al.'s probability, and
/// rifts that fail and fuse again. Every event rebases the plates it changes (<see cref="PlateFrames.Rebase(int, ReadOnlySpan{int}, ReadOnlySpan{int})"/>).
/// </summary>
public static class Events
{
    private const ulong RiftTag = 0x52494654UL; // "RIFT"

    /// <summary>Speed at which a rifted plate moves away from the rift, as a share of <see cref="Pipeline.PlateSettings.MaxSpeedMmPerYr"/> (calibrated).</summary>
    public const double RiftSpeedShare = 1.0;

    /// <summary>Growth cost factor for continental cells during an oceanic split (low: fronts race through continents).</summary>
    public const double OceanicSplitContinentCost = 0.05;

    /// <summary>How strongly noise varies the growth cost of the two regions that split a plate (0 = straight bisector; calibrated).</summary>
    public const double RiftWanderStrength = 0.9;

    /// <summary>Lattice frequency of the rift noise on the unit sphere (bends per rift).</summary>
    public const double RiftWanderFrequency = 6;

    /// <summary>
    /// Merges plate <paramref name="b"/> into plate <paramref name="a"/>: one rotation by area weighting, the suture marked
    /// on continental cells where the two met, plate ids renumbered densely (ids above the removed one move down by one).
    /// </summary>
    public static void Fuse(StepState s, int a, int b) => Fuse(s, a, b, sliver: false);

    // sliver: plate b is a resampling sliver absorbed into a; a keeps its rotation and its collision contacts.
    private static void Fuse(StepState s, int a, int b, bool sliver)
    {
        if (a == b) return;
        if (b < a && !sliver) (a, b) = (b, a);
        var n = s.Grid.CellCount;
        int areaA = 0, areaB = 0;
        for (var c = 0; c < n; c++)
        {
            if (s.PlateOfCell[c] == a) areaA++;
            else if (s.PlateOfCell[c] == b) areaB++;
        }

        var r = s.Pool;
        for (var c = 0; c < n; c++)
        {
            if (s.PlateOfCell[c] != a && s.PlateOfCell[c] != b) continue;
            if (r.Kind[s.SlotOfCell[c]] == CrustKind.Oceanic) continue;
            foreach (var nb in s.Grid.Neighbours(c))
            {
                var other = s.PlateOfCell[nb];
                if ((other == a || other == b) && other != s.PlateOfCell[c] && r.Kind[s.SlotOfCell[nb]] != CrustKind.Oceanic)
                {
                    r.Flags[s.SlotOfCell[c]] |= CrustFlags.Suture;
                    break;
                }
            }
        }

        var total = Math.Max(1, areaA + areaB);
        var omega = (s.Omega(a) * areaA + s.Omega(b) * areaB) * (1.0 / total);
        if (!sliver) SetOmega(s, a, omega);
        if (!sliver && b < s.BaseStepMicroRad.Count)
            s.BaseStepMicroRad[a] = (long)Math.Round((s.BaseStepMicroRad[a] * (double)areaA + s.BaseStepMicroRad[b] * (double)areaB) / total);
        for (var c = 0; c < n; c++)
            if (s.PlateOfCell[c] == b) s.PlateOfCell[c] = a;
        s.Frames.Rebase(a, s.PlateOfCell, s.SlotOfCell);
        RemovePlate(s, b, mergedInto: sliver ? -1 : a);
        if (sliver && b < a) s.Frames.Rebase(a - 1, s.PlateOfCell, s.SlotOfCell);
    }

    /// <summary>Chance that <paramref name="plate"/> rifts in one step: 1 − exp(−λ), λ = λ0 · StepMy/100 · max(continental share, oceanic weight) · cells/A0 · plate-count steering.</summary>
    public static double RiftProbability(StepState s, int plate) =>
        1 - Math.Exp(-(ContinentalRiftLambda(s, plate) + OceanicRiftLambda(s, plate)));

    /// <summary>Continental breakup rate per step (Cortial et al.): λ0 · StepMy/100 · continental share · cells/A0; not steered.</summary>
    public static double ContinentalRiftLambda(StepState s, int plate)
    {
        var (cells, continental) = Counts(s, plate);
        if (cells < 4 || continental < 4) return 0;
        return s.T.RiftRateBpPer100My / 10_000.0 * s.T.StepMy / 100.0 * (continental / (double)cells) * (cells / s.ReferencePlateCells);
    }

    /// <summary>
    /// Oceanic split rate per step (ridge jumps, microplates): λ0 · StepMy/100 · oceanic share · OceanicRiftShareBp ·
    /// cells/A0, steered toward <see cref="Pipeline.TectonicsSettings.TargetPlateCount"/> by (target / count)², limited to
    /// 0.1–10. Only these splits are steered, so keeping the plate count up does not break continents more often.
    /// </summary>
    public static double OceanicRiftLambda(StepState s, int plate)
    {
        var (cells, continental) = Counts(s, plate);
        var oceanic = cells - continental;
        if (oceanic < 4) return 0;
        var steer = Math.Clamp(Math.Pow(s.T.TargetPlateCount / (double)Math.Max(1, s.PlateCount), 2), 0.1, 10);
        return s.T.RiftRateBpPer100My / 10_000.0 * s.T.StepMy / 100.0 * (oceanic / (double)cells) * s.T.OceanicRiftShareBp / 10_000.0
            * (cells / s.ReferencePlateCells) * steer;
    }

    /// <summary>Rolls a rift for every plate (in id order, with this step's sub-seed): continental breakup or oceanic split by their rates.</summary>
    public static IReadOnlyList<int> RollRifts(StepState s)
    {
        var rng = new Xoshiro256StarStar(DeterministicHash.Combine(s.Seed, RiftTag, (ulong)s.StepIndex, 0));
        var rifted = new List<int>();
        var plates = s.PlateCount;
        for (var p = 0; p < plates; p++)
        {
            var lc = ContinentalRiftLambda(s, p);
            var lo = OceanicRiftLambda(s, p);
            var roll = Unit(rng.NextULong());
            var pick = Unit(rng.NextULong());
            if (roll >= 1 - Math.Exp(-(lc + lo))) continue;
            if (Rift(s, p, oceanic: pick * (lc + lo) >= lc) >= 0) rifted.Add(p);
        }

        return rifted;
    }

    private static (int Cells, int Continental) Counts(StepState s, int plate)
    {
        int cells = 0, continental = 0;
        for (var c = 0; c < s.Grid.CellCount; c++)
        {
            if (s.PlateOfCell[c] != plate) continue;
            cells++;
            if (s.Pool.Kind[s.SlotOfCell[c]] == CrustKind.Continental) continental++;
        }

        return (cells, continental);
    }

    /// <summary>
    /// Splits <paramref name="plate"/> along a noisy line through its continental crust (through failed-rift crust when it
    /// has some). The new plate moves away from the line; both get <see cref="CrustRecord.RiftAgeMy"/> 0 along it and the
    /// line crust thins by 10%. With probability <see cref="Pipeline.TectonicsSettings.FailedRiftBp"/> the rift is
    /// scheduled to fail. Returns the new plate id, or −1 when the plate cannot split.
    /// </summary>
    public static int Rift(StepState s, int plate, bool oceanic = false)
    {
        var n = s.Grid.CellCount;
        var rng = new Xoshiro256StarStar(DeterministicHash.Combine(s.Seed, RiftTag, (ulong)s.StepIndex, (ulong)plate + 1));
        var r = s.Pool;
        var members = Enumerable.Range(0, n).Where(c => s.PlateOfCell[c] == plate).ToList();
        var continental = members.Where(c => r.Kind[s.SlotOfCell[c]] == CrustKind.Continental).ToList();
        var oceanCells = members.Where(c => r.Kind[s.SlotOfCell[c]] == CrustKind.Oceanic).ToList();
        if (continental.Count < 4) oceanic = true; // a plate without continent splits in its ocean floor
        if (oceanic) continental = oceanCells; // the pivot set below: ocean floor for a split, continent for a breakup
        if (continental.Count < 4) return -1;
        var failed = oceanic ? [] : continental.Where(c => (r.Flags[s.SlotOfCell[c]] & CrustFlags.FailedRift) != 0).ToList();
        // Two seed cells on either side of the breakup point (a failed rift when the plate has one, else a random
        // continental cell), a quarter of the continental extent apart along a random direction.
        var pivot = failed.Count > 0 ? failed[rng.NextInt(failed.Count)] : continental[rng.NextInt(continental.Count)];
        var x0 = s.Geo.Centre(pivot);
        var random = new Vector3d(Unit(rng.NextULong()) - 0.5, Unit(rng.NextULong()) - 0.5, Unit(rng.NextULong()) - 0.5);
        var normal = (random - x0 * Vector3d.Dot(random, x0));
        if (normal.Length < 1e-9) normal = Vector3d.Cross(x0, new Vector3d(0, 0, 1));
        normal = normal.Normalize();
        var reach = Math.Max(2 * s.CellAngle, 0.25 * Math.Sqrt(continental.Count) * s.CellAngle);
        var seedA = Nearest(s, members, x0 * Math.Cos(reach) - normal * Math.Sin(reach));
        var seedB = Nearest(s, members, x0 * Math.Cos(reach) + normal * Math.Sin(reach));
        if (seedA == seedB) return -1;

        // Both regions grow over the plate with noise-varied step costs; the breakup follows where they meet, which
        // bends and branches (a flat cut leaves straight coasts and wedge-shaped fragments).
        var side = new bool[n];
        var noiseSeed = DeterministicHash.Combine(s.Seed, RiftTag, (ulong)s.StepIndex, (ulong)plate + 0x100);
        var region = new int[n];
        Array.Fill(region, -1);
        var queue = new PriorityQueue<(int Cell, int Region), (double Cost, int Cell)>(
            Comparer<(double Cost, int Cell)>.Create((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost) : a.Cell.CompareTo(b.Cell)));
        queue.Enqueue((seedA, 0), (0, seedA));
        queue.Enqueue((seedB, 1), (0, seedB));
        while (queue.TryDequeue(out var item, out var pri))
        {
            if (region[item.Cell] >= 0) continue;
            region[item.Cell] = item.Region;
            foreach (var nb in s.Grid.Neighbours(item.Cell))
            {
                if (s.PlateOfCell[nb] != plate || region[nb] >= 0) continue;
                var noise = Noise.SphereNoise.Sample(s.Geo.Centre(nb), noiseSeed, 4, RiftWanderFrequency, 0.55, 2.0);
                var step = Math.Max(0.05, 1 + RiftWanderStrength * noise);
                // An oceanic split crosses continental crust cheaply, so the regions meet in the ocean floor and a
                // continent ends whole on one side.
                if (oceanic && r.Kind[s.SlotOfCell[nb]] != CrustKind.Oceanic) step *= OceanicSplitContinentCost;
                queue.Enqueue((nb, item.Region), (pri.Cost + step, nb));
            }
        }

        foreach (var c in members) side[c] = region[c] == 1;
        if (members.All(c => side[c]) || members.All(c => !side[c])) return -1;

        // New plate: the old rotation plus a component that moves it away from the line along the normal.
        var awaySpeed = s.Settings.Plates.MaxSpeedMmPerYr * RiftSpeedShare * s.T.StepMy / (double)s.Settings.Grid.PlanetRadiusKm; // radians per step
        var omega = s.Omega(plate) + Vector3d.Cross(x0, normal) * awaySpeed;
        var created = s.Frames.AddPlate(new Vector3d(0, 0, 1));
        s.Axis.Add(new Vector3d(0, 0, 1));
        s.StepMicroRad.Add(0);
        SetOmega(s, created, omega);
        foreach (var c in members)
            if (side[c]) s.PlateOfCell[c] = created;

        var lineSlots = new List<int>();
        foreach (var c in members)
        {
            var onLine = false;
            foreach (var nb in s.Grid.Neighbours(c))
                if ((s.PlateOfCell[nb] == plate || s.PlateOfCell[nb] == created) && s.PlateOfCell[nb] != s.PlateOfCell[c]) onLine = true;
            if (!onLine) continue;
            var slot = s.SlotOfCell[c];
            r.RiftAgeMy[slot] = 0;
            r.ThicknessDam[slot] = (ushort)Math.Max(CrustRecord.OceanicThicknessDam, r.ThicknessDam[slot] * 9 / 10);
            lineSlots.Add(slot);
        }

        s.Frames.Rebase(plate, s.PlateOfCell, s.SlotOfCell);
        s.Frames.Rebase(created, s.PlateOfCell, s.SlotOfCell);
        if (rng.NextInt(10_000) < s.T.FailedRiftBp)
            s.Scheduled.Add(new ScheduledRefusion(plate, created, s.StepIndex + s.T.FailedRiftSteps, lineSlots));
        return created;
    }

    /// <summary>Fuses failed rifts whose time has come and marks their line crust <see cref="CrustFlags.FailedRift"/>.</summary>
    public static void ProcessScheduled(StepState s)
    {
        foreach (var due in s.Scheduled.Where(x => x.AtStep <= s.StepIndex).ToList())
        {
            s.Scheduled.Remove(due);
            if (due.A < 0 || due.B < 0 || due.A >= s.PlateCount || due.B >= s.PlateCount || due.A == due.B) continue;
            foreach (var slot in due.LineSlots) s.Pool.Flags[slot] |= CrustFlags.FailedRift;
            Fuse(s, due.A, due.B);
        }
    }

    /// <summary>Fuses every candidate pair from <see cref="StepState.FusionCandidates"/>, highest ids first so earlier pairs keep their ids.</summary>
    public static void FuseCandidates(StepState s)
    {
        var pairs = s.FusionCandidates();
        if (pairs.Count == 0) return;
        var merged = new HashSet<int>();
        foreach (var (a, b) in pairs.OrderByDescending(p => p.B).ThenByDescending(p => p.A))
        {
            if (merged.Contains(a) || merged.Contains(b)) continue;
            Fuse(s, a, b);
            merged.Add(a);
            merged.Add(b);
        }
    }

    /// <summary>Plates below this share of the globe's cells are slivers and merge into a neighbour (0.2%: about 80 cells at the default grid).</summary>
    public const double MinPlateShare = 0.002;

    /// <summary>
    /// Merges plates smaller than <see cref="MinPlateShare"/> of the cells (at least 4) into the neighbouring plate they
    /// share the most boundary with (resampling slivers left along boundaries; they are not plates in any useful sense).
    /// </summary>
    public static void MergeSliverPlates(StepState s)
    {
        var limit = Math.Max(4, (int)(MinPlateShare * s.Grid.CellCount));
        while (s.PlateCount > 1)
        {
            var cells = new int[s.PlateCount];
            foreach (var p in s.PlateOfCell) cells[p]++;
            var sliver = Enumerable.Range(0, s.PlateCount).Where(p => cells[p] > 0 && cells[p] < limit).DefaultIfEmpty(-1).Max();
            if (sliver < 0) return;
            var shared = new Dictionary<int, int>();
            for (var c = 0; c < s.Grid.CellCount; c++)
            {
                if (s.PlateOfCell[c] != sliver) continue;
                foreach (var nb in s.Grid.Neighbours(c))
                    if (s.PlateOfCell[nb] != sliver) shared[s.PlateOfCell[nb]] = shared.GetValueOrDefault(s.PlateOfCell[nb]) + 1;
            }

            if (shared.Count == 0) return;
            var into = shared.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            Fuse(s, into, sliver, sliver: true);
        }
    }

    /// <summary>Removes plates that no longer own any cell (all their crust subducted or merged), highest id first.</summary>
    public static void RemoveEmptyPlates(StepState s)
    {
        var cells = new int[s.PlateCount];
        foreach (var p in s.PlateOfCell) cells[p]++;
        for (var p = s.PlateCount - 1; p >= 0; p--)
            if (cells[p] == 0 && s.PlateCount > 1) RemovePlate(s, p);
    }

    private static void SetOmega(StepState s, int plate, Vector3d omega)
    {
        var len = omega.Length;
        var axis = len > 1e-15 ? omega * (1 / len) : s.Axis[plate];
        s.Axis[plate] = axis;
        s.StepMicroRad[plate] = (long)Math.Round(len * 1e6);
        s.Frames.SetAxis(plate, axis);
    }

    // Removes plate b and renumbers the rest. Contact history of other pairs is kept (renumbered); pairs with b, or with
    // the plate it merged into, are forgotten (the merged plate's contacts start over).
    private static void RemovePlate(StepState s, int b, int mergedInto = -1)
    {
        var map = new int[s.PlateCount];
        for (var p = 0; p < map.Length; p++) map[p] = p < b ? p : p == b ? -1 : p - 1;
        s.Frames.Renumber(map);
        s.Axis.RemoveAt(b);
        s.StepMicroRad.RemoveAt(b);
        if (b < s.BaseStepMicroRad.Count) s.BaseStepMicroRad.RemoveAt(b);
        for (var c = 0; c < s.PlateOfCell.Length; c++)
            if (s.PlateOfCell[c] > b) s.PlateOfCell[c]--;
        foreach (var x in s.Scheduled)
        {
            x.A = x.A < 0 || x.A >= map.Length ? -1 : map[x.A];
            x.B = x.B < 0 || x.B >= map.Length ? -1 : map[x.B];
        }

        s.RemapContacts(map, mergedInto < 0 ? [b] : [b, mergedInto]);
    }

    private static int Nearest(StepState s, List<int> cells, Vector3d target)
    {
        var best = cells[0];
        var bestDot = double.NegativeInfinity;
        foreach (var c in cells)
        {
            var d = Vector3d.Dot(s.Geo.Centre(c), target);
            if (d > bestDot) { bestDot = d; best = c; }
        }

        return best;
    }

    private static Vector3d Centroid(GlobeGeometry geo, IReadOnlyList<int> cells)
    {
        double x = 0, y = 0, z = 0;
        foreach (var c in cells)
        {
            var v = geo.Centre(c);
            x += v.X; y += v.Y; z += v.Z;
        }

        var p = new Vector3d(x, y, z);
        return p.Length < 1e-9 ? geo.Centre(cells[0]) : p.Normalize();
    }

    private static double Unit(ulong v) => (v >> 11) * (1.0 / ((1UL << 53) - 1));
}
