using System.Text.Json;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Seeding;

/// <summary>Reads a generation settings file: the seed <c>generation</c> block shape (see <c>globe defaults</c>).</summary>
public static class GenerationSettingsFile
{
    /// <summary>
    /// Reads a generation settings file (the seed <c>generation</c> block shape) and validates it. Throws
    /// <see cref="InvalidOperationException"/> naming the file on a missing file, malformed JSON, an unknown setting or an
    /// out-of-range value. Properties the file does not name take the values of <paramref name="baseSettings"/> (a preset;
    /// the defaults when null).
    /// </summary>
    public static WorldGenSettings Read(string path, WorldGenSettings? baseSettings = null)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Generation file '{path}' does not exist.");
        WorldGenSettings settings;
        try
        {
            settings = WorldGenSettingsJson.Deserialize(File.ReadAllText(path), baseSettings ?? new WorldGenSettings());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Generation file '{path}' invalid: {ex.Message}", ex);
        }

        var valid = settings.Validate();
        if (valid.IsError)
            throw new InvalidOperationException($"Generation file '{path}' invalid: {string.Join(" ", valid.Errors.Select(e => e.Description))}");
        return settings;
    }

}
