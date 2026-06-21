namespace WorldEcon.Seeding;

/// <summary>A source that yields a fully-authored <see cref="SeedWorld"/> ready to import.</summary>
public interface ISeedSource
{
    Task<SeedWorld> LoadAsync(CancellationToken ct = default);
}
