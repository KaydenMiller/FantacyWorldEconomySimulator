namespace WorldEcon.Seeding;

/// <summary>
/// STUB: imports a world from a Kanka campaign (https://kanka.io). Not yet implemented.
/// <para>
/// The planned implementation is two-layer:
/// </para>
/// <list type="number">
///   <item><b>Structural extraction</b> — walk the Kanka entity graph (locations → continents,
///   countries, regions; places/points → settlements; map connections → routes) to build the
///   geography hierarchy and the route network.</item>
///   <item><b>Economic extraction</b> — read campaign-defined attributes (goods catalogs, recipes,
///   shop inventories, endowments, merchant capital) off entities and their attribute templates to
///   populate the economic layer.</item>
/// </list>
/// <para>
/// The economic layer is blocked on agreeing a canonical Kanka attribute schema (spec §14.2 OPEN —
/// the "campaign attribute audit"). Until that lands, author worlds as JSON and use
/// <see cref="JsonSeedSource"/>.
/// </para>
/// </summary>
public sealed class KankaSeedSource(long campaignId, string apiToken) : ISeedSource
{
    private long CampaignId { get; } = campaignId;
    private string ApiToken { get; } = apiToken;

    public Task<SeedWorld> LoadAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "Kanka import is pending the campaign attribute audit (spec §14.2 OPEN). Use JsonSeedSource for now.");
}
