using WorldEcon.Simulation.Random;

namespace WorldEcon.WorldGen.Pipeline.Checkpoints;

/// <summary>
/// A settings section as a globe stage reads it, for checkpoint fingerprints. <see cref="GridGlobe"/> is the part of
/// <see cref="GridSettings"/> the globe stages read (<see cref="GridSettings.Frequency"/>,
/// <see cref="GridSettings.PlanetRadiusKm"/>); the region and tile multipliers are read only by tile stages, so a
/// checkpoint made under one tile resolution resumes under another. The settings JSON (JavaScript Object Notation) format
/// is not split.
/// </summary>
public enum SettingsSection
{
    GridGlobe,
    Plates,
    Elevation,
    SeaLevel,
    Currents,
    Winds,
    Climate,
    Tectonics,
}

/// <summary>One stage's part of a fingerprint: its code version and the hash of each settings section it reads.</summary>
public sealed record StagePrint(WorldGenStage Stage, int CodeVersion, IReadOnlyDictionary<SettingsSection, ulong> SectionHashes)
{
    public bool Equals(StagePrint? other) =>
        other is not null && Stage == other.Stage && CodeVersion == other.CodeVersion &&
        SectionHashes.Count == other.SectionHashes.Count &&
        SectionHashes.All(kv => other.SectionHashes.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public override int GetHashCode() => HashCode.Combine(Stage, CodeVersion, SectionHashes.Count);
}

/// <summary>
/// Checkpoint fingerprints. A checkpoint taken after stage N is valid for a run only when the world seed,
/// <see cref="WorldGenerator.GeneratorVersion"/>, and every stage up to N (its code version and the settings sections it
/// declares in <see cref="WorldGenerator.GlobeStages"/>) are the same.
/// </summary>
public static class StageFingerprint
{
    private const ulong SectionTag = 0x53454354494F4E53UL; // "SECTIONS"
    private const ulong PrintTag = 0x46494E4745525052UL; // "FINGERPR"

    /// <summary>Hash of the integer values of <paramref name="section"/>, in declaration order, tagged with the section.</summary>
    public static ulong SectionHash(WorldGenSettings settings, SettingsSection section)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var h = DeterministicHash.Combine(SectionTag, (ulong)section, 0, 0);
        foreach (var v in Values(settings, section))
            h = DeterministicHash.Combine(h, unchecked((ulong)(long)v), 0, 0);
        return h;
    }

    /// <summary>The prints of every globe stage from the first up to and including <paramref name="upToInclusive"/>.</summary>
    public static IReadOnlyList<StagePrint> For(WorldGenSettings settings, WorldGenStage upToInclusive) =>
        For(settings, upToInclusive, WorldGenerator.GlobeStages);

    /// <summary>As the public overload, over the given stage list (tests run fake stages).</summary>
    internal static IReadOnlyList<StagePrint> For(WorldGenSettings settings, WorldGenStage upToInclusive, IReadOnlyList<WorldGenerator.StageEntry> stages)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var last = -1;
        for (var i = 0; i < stages.Count; i++)
            if (stages[i].Stage == upToInclusive) last = i;
        if (last < 0)
            throw new ArgumentException($"{upToInclusive} is not a globe stage; only globe stages can be checkpointed.", nameof(upToInclusive));

        var prints = new List<StagePrint>(last + 1);
        for (var i = 0; i <= last; i++)
        {
            var e = stages[i];
            prints.Add(new StagePrint(e.Stage, e.CodeVersion, e.Reads.ToDictionary(r => r, r => SectionHash(settings, r))));
        }

        return prints;
    }

    /// <summary>One value for the seed, generator version and every print, in order.</summary>
    public static ulong Combine(ulong worldSeed, int generatorVersion, IReadOnlyList<StagePrint> prints)
    {
        ArgumentNullException.ThrowIfNull(prints);
        var h = DeterministicHash.Combine(PrintTag, worldSeed, unchecked((ulong)(long)generatorVersion), (ulong)prints.Count);
        foreach (var p in prints)
        {
            h = DeterministicHash.Combine(h, (ulong)p.Stage, unchecked((ulong)(long)p.CodeVersion), 0);
            foreach (var kv in p.SectionHashes.OrderBy(kv => kv.Key))
                h = DeterministicHash.Combine(h, (ulong)kv.Key, kv.Value, 0);
        }

        return h;
    }

    /// <summary>
    /// Every difference between a stored fingerprint and the current one, one line each: seed, generator version, then
    /// per stage its code version and each section that differs (e.g. <c>Elevation: section SeaLevel differs</c>). Empty
    /// when they match.
    /// </summary>
    public static IReadOnlyList<string> Differences(
        IReadOnlyList<StagePrint> stored, ulong storedSeed, int storedVersion,
        IReadOnlyList<StagePrint> current, ulong currentSeed, int currentVersion)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(current);
        var diff = new List<string>();
        if (storedSeed != currentSeed) diff.Add($"seed differs ({storedSeed} → {currentSeed})");
        if (storedVersion != currentVersion) diff.Add($"generator version differs ({storedVersion} → {currentVersion})");

        var byStage = current.ToDictionary(p => p.Stage);
        foreach (var s in stored)
        {
            if (!byStage.TryGetValue(s.Stage, out var c))
            {
                diff.Add($"{s.Stage}: stage is not in the current run");
                continue;
            }

            if (s.CodeVersion != c.CodeVersion) diff.Add($"{s.Stage}: code version differs ({s.CodeVersion} → {c.CodeVersion})");
            foreach (var section in s.SectionHashes.Keys.Union(c.SectionHashes.Keys).OrderBy(k => k))
            {
                var inStored = s.SectionHashes.TryGetValue(section, out var sv);
                var inCurrent = c.SectionHashes.TryGetValue(section, out var cv);
                if (!inStored || !inCurrent) diff.Add($"{s.Stage}: reads of section {section} differ");
                else if (sv != cv) diff.Add($"{s.Stage}: section {section} differs");
            }
        }

        foreach (var c in current)
            if (stored.All(s => s.Stage != c.Stage)) diff.Add($"{c.Stage}: stage is not in the checkpoint");

        return diff;
    }

    private static IEnumerable<int> Values(WorldGenSettings s, SettingsSection section)
    {
        if (section == SettingsSection.GridGlobe) return [s.Grid.Frequency, s.Grid.PlanetRadiusKm];
        object value = section switch
        {
            SettingsSection.Plates => s.Plates,
            SettingsSection.Elevation => s.Elevation,
            SettingsSection.SeaLevel => s.SeaLevel,
            SettingsSection.Currents => s.Currents,
            SettingsSection.Winds => s.Winds,
            SettingsSection.Climate => s.Climate,
            SettingsSection.Tectonics => s.Tectonics,
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
        };
        return WorldGenSettingsJson.Leaves(value.GetType()).Select(p => (int)p.GetValue(value)!);
    }
}
