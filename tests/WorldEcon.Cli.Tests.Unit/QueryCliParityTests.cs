using FluentAssertions;
using WorldEcon.Application.Queries;
using WorldEcon.TestFixtures;

namespace WorldEcon.Cli.Tests.Unit;

/// <summary>The GUI's Application queries agree with the CLI commands on the same database.</summary>
[NotInParallel]
public class QueryCliParityTests
{
    internal static async Task<string> RunCliAsync(params string[] args)
    {
        var context = TestContext.Current!;
        var outStart = context.Output.GetStandardOutput().Length;
        var exit = await CommandRunner.RunAsync(args);
        exit.Should().Be(0);
        return context.Output.GetStandardOutput()[outStart..];
    }

    /// <summary>Table rows of CLI output: lines indented by two spaces, without the header line.</summary>
    internal static List<string[]> Rows(string output) => output.Split('\n')
        .Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Trim().Length > 0)
        .Skip(1)
        .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .ToList();

    [Test]
    public async Task Caravans_MatchCliCaravans()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var cli = Rows(await RunCliAsync("caravans", w.Path))
            .Where(r => r.Contains("no"))   // Delivered column
            .Select(r => (Origin: r[0], Dest: r[1], Good: r[2], Qty: long.Parse(r[3])))
            .OrderBy(x => x.Origin).ThenBy(x => x.Good).ToList();
        var query = (await CaravanPositionsQuery.RunAsync(w.Path)).Value.Caravans
            .Select(c => (Origin: c.OriginName, Dest: c.DestinationName, Good: c.GoodName, Qty: c.Quantity))
            .OrderBy(x => x.Origin).ThenBy(x => x.Good).ToList();
        query.Should().Equal(cli);
        query.Should().HaveCount(2);
    }

    [Test]
    public async Task Market_MatchesCliStock()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var cli = await RunCliAsync("stock", w.Path, "Ashford");
        var lines = cli.Split('\n').Where(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Trim().Length > 0).Skip(1).ToList();
        var market = (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.Market;
        lines.Should().HaveCount(market.Count);
        for (var i = 0; i < market.Count; i++)
        {
            var m = market[i];
            var c = w.World.Currency;
            lines[i].Should().Be($"  {m.Good,-16} {m.Quantity,10} {c.Format(new WorldEcon.SharedKernel.Money(m.MinCostBasis)),12} {c.Format(new WorldEcon.SharedKernel.Money(m.Price)),14}".TrimEnd('\r'));
        }
    }

    [Test]
    public async Task Shops_MarginMatchesCliPrice()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var cli = await RunCliAsync("price", w.Path, "Ashford", "Grain");
        var shops = (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.Shops;
        foreach (var shop in shops)
        {
            var line = shop.Stock.Single(s => s.Good == "Grain");
            var cliLine = cli.Split('\n').Single(l => l.TrimStart().StartsWith(shop.Name, StringComparison.Ordinal));
            cliLine.Should().Contain($" {line.Quantity,6} ");
            cliLine.TrimEnd().Should().EndWith($"{line.MarginPct,9:0.##}%");
        }
    }

    [Test]
    public async Task TravelTimes_MatchCliKnowledge()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var cli = Rows(await RunCliAsync("knowledge", w.Path, "Ashford"))
            .Select(r => (From: r[0], To: r[1], Vehicle: r[2], Days: r[3], Source: r[5])).ToList();
        var rows = (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.TravelTimes
            .Select(t => (From: t.From, To: t.To, Vehicle: t.VehicleClass,
                Days: (t.Ticks / 1440.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), Source: t.Source.ToString()))
            .ToList();
        rows.Should().Equal(cli);
    }

    [Test]
    public async Task Merchants_MatchCliMerchants()
    {
        using var w = await EconomyTestWorld.CreateAsync();
        var cli = (await RunCliAsync("merchants", w.Path)).Split('\n').Single(l => l.TrimStart().StartsWith("Ashford", StringComparison.Ordinal));
        var m = (await SettlementDetailsQuery.RunAsync(w.Path, w.Ashford.Id.Value)).Value.Merchants.Single();
        cli.Should().Contain($" {m.Reach,8} {m.VehicleClass,-14} {m.RiskAversion,5} ");
        foreach (var t in m.LastTrips) cli.Should().Contain($"{t.From}→{t.To} by {t.VehicleClass}");
    }
}
