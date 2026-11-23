using System.Text.Json;
using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.Seeding;

/// <summary>
/// Loads a <see cref="SeedWorld"/> from a JSON file on disk. A <c>generation</c> block with an unknown setting name,
/// a non-object section or a non-integer value throws <see cref="InvalidOperationException"/>
/// <c>Seed invalid: generation: unknown setting 'Section.Name'</c> (or the matching message).
/// </summary>
public sealed class JsonSeedSource(string filePath) : ISeedSource
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<SeedWorld> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new InvalidOperationException($"Seed file not found: '{filePath}'.");

        await using var stream = File.OpenRead(filePath);
        SeedWorld? seed;
        try
        {
            seed = await JsonSerializer.DeserializeAsync<SeedWorld>(stream, Options, ct);
        }
        catch (WorldGenSettingsJsonException ex)
        {
            throw new InvalidOperationException($"Seed invalid: generation: {ex.Message}", ex);
        }

        if (seed is null)
            throw new InvalidOperationException($"Seed file '{filePath}' deserialized to null (empty or 'null' content?).");

        return seed;
    }
}
