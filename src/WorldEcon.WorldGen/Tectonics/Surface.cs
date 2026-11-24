namespace WorldEcon.WorldGen.Tectonics;

/// <summary>
/// Surface processes per step (design section 5, items 5–6): erosion with sediment deposited on lower neighbours,
/// coal and evaporite exposure by latitude belt, and ageing of the record. Each crust slot is processed once per step even
/// when resampling shows it in two cells.
/// </summary>
public static class Surface
{
    /// <summary>Wet tropical belt for coal-forming swamps: |latitude| below this, degrees. Research marks belt bounds unverified; calibrated.</summary>
    public const double WetTropicsMaxLat = 10;

    /// <summary>Wet temperate belt for coal, degrees.</summary>
    public const double WetTemperateMinLat = 40;

    public const double WetTemperateMaxLat = 60;

    /// <summary>Arid subtropical belt for evaporites, degrees.</summary>
    public const double AridMinLat = 15;

    public const double AridMaxLat = 35;

    /// <summary>Crust within this many dam of the continental base counts as low land or basin (±3 km of crust ≈ ±450 m of height).</summary>
    public const int LowLandBandDam = 300;

    /// <summary>
    /// Mountain thickness above the continental base decays by exp(−StepMy/ErosionEFoldMy); the eroded thickness is shared
    /// equally as sediment by the neighbours with thinner crust (a proxy for lower ground), updating their deepest burial.
    /// </summary>
    public static void Erode(StepState s)
    {
        var r = s.Pool;
        var n = s.Grid.CellCount;
        var factor = Math.Exp(-s.T.StepMy / (double)s.T.ErosionEFoldMy);
        var before = new ushort[n];
        for (var c = 0; c < n; c++) before[c] = r.ThicknessDam[s.SlotOfCell[c]];
        var done = new HashSet<int>();
        for (var c = 0; c < n; c++)
        {
            var slot = s.SlotOfCell[c];
            if (r.Kind[slot] == CrustKind.Oceanic || !done.Add(slot)) continue;
            var excess = before[c] - CrustRecord.ContinentalThicknessDam;
            if (excess <= 0) continue;
            var kept = (int)Math.Round(excess * factor);
            var eroded = excess - kept;
            if (eroded <= 0) continue;
            r.ThicknessDam[slot] = (ushort)(CrustRecord.ContinentalThicknessDam + kept);
            var lower = new List<int>(6);
            foreach (var nb in s.Grid.Neighbours(c))
                if (before[nb] < before[c] && s.SlotOfCell[nb] != slot) lower.Add(nb);
            if (lower.Count == 0) continue;
            var share = eroded / lower.Count;
            foreach (var nb in lower)
            {
                var ns = s.SlotOfCell[nb];
                r.SedimentDam[ns] = (ushort)Math.Min(ushort.MaxValue - 1, r.SedimentDam[ns] + share);
                r.MaxBurialDam[ns] = Math.Max(r.MaxBurialDam[ns], r.SedimentDam[ns]);
            }
        }
    }

    /// <summary>
    /// Coal exposure: continental low land in a wet belt. Evaporite exposure: in the arid belt, a young rift (≤ 30 My),
    /// young passive margin (≤ 30 My) or sediment-filled low land. Each adds <see cref="Pipeline.TectonicsSettings.StepMy"/>.
    /// </summary>
    public static void Expose(StepState s)
    {
        var r = s.Pool;
        var step = s.T.StepMy;
        var done = new HashSet<int>();
        for (var c = 0; c < s.Grid.CellCount; c++)
        {
            var slot = s.SlotOfCell[c];
            if (!done.Add(slot)) continue;
            var lat = Math.Abs(Math.Asin(Math.Clamp(s.Geo.Centre(c).Z, -1, 1)) * 180 / Math.PI);
            var continental = r.Kind[slot] == CrustKind.Continental;
            var low = continental && Math.Abs(r.ThicknessDam[slot] - CrustRecord.ContinentalThicknessDam) <= LowLandBandDam;
            var wet = lat < WetTropicsMaxLat || (lat >= WetTemperateMinLat && lat <= WetTemperateMaxLat);
            if (low && wet) r.CoalExposureMy[slot] = Add(r.CoalExposureMy[slot], step);
            var arid = lat >= AridMinLat && lat <= AridMaxLat;
            var basin = r.RiftAgeMy[slot] <= 30 || r.PassiveMarginAgeMy[slot] <= 30 || (low && r.SedimentDam[slot] > 0);
            if (arid && basin) r.EvaporiteExposureMy[slot] = Add(r.EvaporiteExposureMy[slot], step);
        }
    }

    /// <summary>Advances every age field of every slot by one step; <see cref="CrustRecord.Never"/> stays never.</summary>
    public static void AgeRecord(StepState s)
    {
        var r = s.Pool;
        var step = s.T.StepMy;
        for (var slot = 0; slot < r.Count; slot++)
        {
            r.FormedAgeMy[slot] = Age(r.FormedAgeMy[slot], step);
            r.LastOrogenyAgeMy[slot] = Age(r.LastOrogenyAgeMy[slot], step);
            r.ArcMagmaAgeMy[slot] = Age(r.ArcMagmaAgeMy[slot], step);
            r.RiftAgeMy[slot] = Age(r.RiftAgeMy[slot], step);
            r.PassiveMarginAgeMy[slot] = Age(r.PassiveMarginAgeMy[slot], step);
        }
    }

    private static ushort Age(ushort value, int step) =>
        value == CrustRecord.Never ? CrustRecord.Never : (ushort)Math.Min(CrustRecord.Never - 1, value + step);

    private static ushort Add(ushort value, int step) => (ushort)Math.Min(CrustRecord.Never - 1, value + step);
}
