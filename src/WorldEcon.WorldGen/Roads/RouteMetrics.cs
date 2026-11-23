using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Roads;

/// <summary>Travel properties of one generated route. Factors and difficulty are percentages.</summary>
public sealed record RouteMetricsResult(long LengthMetres, int Difficulty, int SpeedFactorAtoB, int SpeedFactorBtoA, int MinTier, int MaxTier);

public static class RouteMetrics
{
    /// <summary>
    /// Metrics of a path of <paramref name="category"/>:
    /// <list type="bullet">
    /// <item>LengthMetres: the sum of exact step lengths, rounded once.</item>
    /// <item>Land: Difficulty = round(mean undiscounted step cost × 100 / BaseStepCost), at least 1. Direction factors
    /// 100 − UphillPenalty × climb/km + DownhillBonus × descent/km, clamped to [Min, Max]DirectionFactor. MaxTier 1 when a
    /// step is steeper than SteepSlopeMPerKm, else DefaultMaxTier.</item>
    /// <item>Sea: see <see cref="Voyage"/>.</item>
    /// <item>Air: Difficulty 100, factors 100 both ways (no winds yet), MaxTier DefaultMaxTier.</item>
    /// </list>
    /// MinTier is 1.
    /// </summary>
    public static RouteMetricsResult Compute(RoadTerrain terrain, IReadOnlyList<int> tiles, RoadCategory category = RoadCategory.Land)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tiles.Count, 2);
        var s = terrain.Settings;
        double exact = 0;
        for (int i = 1; i < tiles.Count; i++) exact += terrain.StepMetresExact(tiles[i - 1], tiles[i]);
        long length = Math.Max(1, (long)Math.Round(exact));

        if (category == RoadCategory.Air)
            return new RouteMetricsResult(length, 100, 100, 100, 1, s.DefaultMaxTier);

        if (category == RoadCategory.Sea)
            return Voyage(terrain, tiles);

        long costSum = 0, climb = 0, descent = 0;
        bool steep = false;
        for (int i = 1; i < tiles.Count; i++)
        {
            int a = tiles[i - 1], b = tiles[i];
            costSum += terrain.LandStepCost(a, b, onRoad: false);
            int d = terrain.ElevationM(b) - terrain.ElevationM(a);
            if (d > 0) climb += d; else descent -= d;
            long stepM = Math.Max(1, terrain.StepMetres(a, b));
            if ((long)Math.Abs(d) * 1000 / stepM > s.SteepSlopeMPerKm) steep = true;
        }
        int steps = tiles.Count - 1;
        int difficulty = (int)Math.Max(1, (costSum * 100 + (long)steps * s.BaseStepCost / 2) / ((long)steps * s.BaseStepCost));
        long km = Math.Max(1, length / 1000);
        int Factor(long up, long down) => (int)Math.Clamp(100 - s.UphillPenalty * up / km + s.DownhillBonus * down / km,
            s.MinDirectionFactor, s.MaxDirectionFactor);
        return new RouteMetricsResult(length, difficulty, Factor(climb, descent), Factor(descent, climb), 1,
            steep ? 1 : s.DefaultMaxTier);
    }

    /// <summary>
    /// Metrics of a voyage path (port tile, water tiles, port tile): length; difficulty 100; per-direction factor
    /// L ÷ Σ(metres ÷ pct) × 100 where a step is a river step with the flow (<see cref="RoadSettings.RiverDownstreamPct"/>),
    /// against it (<see cref="RoadSettings.RiverUpstreamPct"/>) or open water (100 %); MaxTier
    /// <see cref="RoadSettings.RiverMaxTier"/> when an interior tile is a river, <see cref="RoadSettings.LakeMaxTier"/>
    /// when one is a lake (the lower of the two when both), else <see cref="RoadSettings.DefaultMaxTier"/>.
    /// A step is a river step when either tile is a river tile; it goes with the flow when its first tile drains to its
    /// second, against it when the second drains to the first, otherwise it counts as open water.
    /// </summary>
    public static RouteMetricsResult Voyage(RoadTerrain terrain, IReadOnlyList<int> tiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tiles.Count, 2);
        var s = terrain.Settings;
        double total = 0, timeAtoB = 0, timeBtoA = 0;
        bool river = false, lake = false;
        for (int i = 1; i < tiles.Count; i++)
        {
            int a = tiles[i - 1], b = tiles[i];
            double m = terrain.StepMetresExact(a, b);
            total += m;
            int pctAB = 100, pctBA = 100;
            if (terrain.Water(a) == TileWaterKind.River || terrain.Water(b) == TileWaterKind.River)
            {
                if (terrain.DownstreamOf(a) == b) { pctAB = s.RiverDownstreamPct; pctBA = s.RiverUpstreamPct; }
                else if (terrain.DownstreamOf(b) == a) { pctAB = s.RiverUpstreamPct; pctBA = s.RiverDownstreamPct; }
            }
            timeAtoB += m / pctAB;
            timeBtoA += m / pctBA;
        }
        for (int i = 1; i < tiles.Count - 1; i++)
        {
            var w = terrain.Water(tiles[i]);
            if (w == TileWaterKind.River) river = true;
            else if (w == TileWaterKind.Lake) lake = true;
        }
        long length = Math.Max(1, (long)Math.Round(total));
        int Factor(double time) => time <= 0 ? 100 : (int)Math.Clamp(Math.Round(total / time), 1, 1_000);
        int max = s.DefaultMaxTier;
        if (river) max = Math.Min(max, s.RiverMaxTier);
        if (lake) max = Math.Min(max, s.LakeMaxTier);
        return new RouteMetricsResult(length, 100, Factor(timeAtoB), Factor(timeBtoA), 1, max);
    }

    /// <summary>
    /// Tile reached after <paramref name="metresAlong"/> metres, walking the path from NodeA (<paramref name="fromA"/>)
    /// or from NodeB. Returns the last tile whose centre has been passed; the end tile once the length is covered.
    /// </summary>
    public static int TileAt(RoadTerrain terrain, IReadOnlyList<int> tiles, bool fromA, long metresAlong)
    {
        int n = tiles.Count;
        int At(int k) => fromA ? tiles[k] : tiles[n - 1 - k];
        double walked = 0;
        for (int k = 1; k < n; k++)
        {
            walked += terrain.StepMetresExact(At(k - 1), At(k));
            if (walked > metresAlong) return At(k - 1);
        }
        return At(n - 1);
    }
}
