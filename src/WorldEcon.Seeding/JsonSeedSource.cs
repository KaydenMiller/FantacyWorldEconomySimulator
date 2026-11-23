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
        catch (JsonException ex) when (ex.Message.Contains(nameof(SeedRoute), StringComparison.Ordinal)
                                       && ex.Message.Contains("could not be mapped", StringComparison.Ordinal))
        {
            var field = System.Text.RegularExpressions.Regex.Match(ex.Message, "'([^']+)'").Groups[1].Value;
            throw new InvalidOperationException(
                $"Seed invalid: route field '{field}' is not supported; roads are generated from the globe (remove distance/terrain).", ex);
        }

        if (seed is null)
            throw new InvalidOperationException($"Seed file '{filePath}' deserialized to null (empty or 'null' content?).");

        return seed;
    }
}
