using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldEcon.WorldGen.Pipeline;

/// <summary>A <c>generation</c> JSON block that names an unknown setting, a non-object section or a non-integer value.</summary>
public sealed class WorldGenSettingsJsonException(string message) : JsonException(message);

/// <summary>
/// JSON form of <see cref="WorldGenSettings"/>: <c>{"Grid": {"Frequency": 64, ...}, "Plates": {...}, ...}</c>. Section and
/// property names match case-insensitively. Omitted sections and properties keep their defaults. An unknown section or
/// property name, a section or property named twice (case-insensitively), a section that is not an object, or a value
/// that is not an integer in the <see cref="int"/> range throws <see cref="WorldGenSettingsJsonException"/>, whose message
/// is <c>unknown setting 'Section.Name'</c> (or <c>unknown setting 'Section'</c>) for a name that does not exist and
/// <c>duplicate setting 'Section.Name'</c> (or <c>duplicate setting 'Section'</c>) for a repeated name. Range checks are not done here; call
/// <see cref="WorldGenSettings.Validate"/>.
/// </summary>
public static class WorldGenSettingsJson
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Section properties of <see cref="WorldGenSettings"/>, in declaration order.</summary>
    internal static readonly PropertyInfo[] Sections = typeof(WorldGenSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .OrderBy(p => p.MetadataToken)
        .ToArray();

    /// <summary>Integer leaf properties of a section type, in declaration order.</summary>
    internal static PropertyInfo[] Leaves(Type section) => section
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(int))
        .OrderBy(p => p.MetadataToken)
        .ToArray();

    public static string Serialize(WorldGenSettings settings, bool indented = false) =>
        indented ? JsonSerializer.Serialize(settings, Indented) : JsonSerializer.Serialize(settings);

    /// <summary>Parses a settings object; see the class comment for the accepted shape.</summary>
    public static WorldGenSettings Deserialize(string json) => Deserialize(json, new WorldGenSettings());

    /// <summary>
    /// Parses a settings object on top of <paramref name="baseSettings"/> (for example a preset from
    /// <see cref="WorldGenPresets.Get"/>): properties named in <paramref name="json"/> replace the base values, every other
    /// property keeps the base value. <paramref name="baseSettings"/> is not modified.
    /// </summary>
    public static WorldGenSettings Deserialize(string json, WorldGenSettings baseSettings)
    {
        ArgumentNullException.ThrowIfNull(baseSettings);
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement, baseSettings);
    }

    internal static WorldGenSettings FromElement(JsonElement root) => FromElement(root, new WorldGenSettings());

    internal static WorldGenSettings FromElement(JsonElement root, WorldGenSettings baseSettings)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new WorldGenSettingsJsonException($"settings must be a JSON object, not {root.ValueKind}");

        var settings = baseSettings with { };
        var seenSections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sectionJson in root.EnumerateObject())
        {
            var sectionProp = Find(Sections, sectionJson.Name)
                ?? throw new WorldGenSettingsJsonException($"unknown setting '{sectionJson.Name}'");
            if (!seenSections.Add(sectionProp.Name))
                throw new WorldGenSettingsJsonException($"duplicate setting '{sectionProp.Name}'");
            if (sectionJson.Value.ValueKind != JsonValueKind.Object)
                throw new WorldGenSettingsJsonException($"setting '{sectionProp.Name}' must be a JSON object, not {sectionJson.Value.ValueKind}");

            var section = sectionProp.GetValue(settings)!;
            var copy = CloneSection(section);
            var leaves = Leaves(sectionProp.PropertyType);
            var seenLeaves = new HashSet<string>(StringComparer.Ordinal);
            foreach (var leafJson in sectionJson.Value.EnumerateObject())
            {
                var leaf = Find(leaves, leafJson.Name)
                    ?? throw new WorldGenSettingsJsonException($"unknown setting '{sectionProp.Name}.{leafJson.Name}'");
                if (!seenLeaves.Add(leaf.Name))
                    throw new WorldGenSettingsJsonException($"duplicate setting '{sectionProp.Name}.{leaf.Name}'");
                if (leafJson.Value.ValueKind != JsonValueKind.Number || !leafJson.Value.TryGetInt32(out var value))
                    throw new WorldGenSettingsJsonException($"setting '{sectionProp.Name}.{leaf.Name}' must be an integer, not {leafJson.Value.GetRawText()}");
                leaf.SetValue(copy, value);
            }

            sectionProp.SetValue(settings, copy);
        }

        return settings;
    }

    internal static void Write(Utf8JsonWriter writer, WorldGenSettings settings)
    {
        writer.WriteStartObject();
        foreach (var sectionProp in Sections)
        {
            writer.WritePropertyName(sectionProp.Name);
            writer.WriteStartObject();
            var section = sectionProp.GetValue(settings)!;
            foreach (var leaf in Leaves(sectionProp.PropertyType))
                writer.WriteNumber(leaf.Name, (int)leaf.GetValue(section)!);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static PropertyInfo? Find(PropertyInfo[] props, string name) =>
        props.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // Sections are records with a parameterless constructor; a fresh instance with the leaves copied keeps the input untouched.
    private static object CloneSection(object section)
    {
        var copy = Activator.CreateInstance(section.GetType())!;
        foreach (var leaf in Leaves(section.GetType()))
            leaf.SetValue(copy, leaf.GetValue(section));
        return copy;
    }
}

/// <summary>Routes every System.Text.Json read and write of <see cref="WorldGenSettings"/> through <see cref="WorldGenSettingsJson"/>.</summary>
public sealed class WorldGenSettingsJsonConverter : JsonConverter<WorldGenSettings>
{
    public override WorldGenSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return WorldGenSettingsJson.FromElement(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, WorldGenSettings value, JsonSerializerOptions options) =>
        WorldGenSettingsJson.Write(writer, value);
}
