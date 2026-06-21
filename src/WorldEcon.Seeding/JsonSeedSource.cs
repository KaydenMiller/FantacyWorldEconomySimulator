using System.Text.Json;

namespace WorldEcon.Seeding;

/// <summary>Loads a <see cref="SeedWorld"/> from a JSON file on disk.</summary>
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
        var seed = await JsonSerializer.DeserializeAsync<SeedWorld>(stream, Options, ct);
        if (seed is null)
            throw new InvalidOperationException($"Seed file '{filePath}' deserialized to null (empty or 'null' content?).");

        return seed;
    }
}
