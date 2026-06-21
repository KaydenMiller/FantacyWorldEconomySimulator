using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Queries;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Repositories;
using WorldEcon.Persistence.Snapshots;

namespace WorldEcon.Cli;

internal static class CommandRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "new" => await CmdNew(args),
                "list" => await CmdList(args),
                "price" => await CmdPrice(args),
                "advance" => await CmdAdvance(args),
                "snapshot" => await CmdSnapshot(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static WorldDbContext OpenContext(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    // ---- new <dbPath> ----
    private static async Task<int> CmdNew(string[] args)
    {
        if (args.Length < 2)
            return MissingArgs("new <dbPath>");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        var world = DemoSeeder.Seed(ctx);

        var settlements = await ctx.Settlements.CountAsync();
        var goods = await ctx.Goods.CountAsync();
        var shops = await ctx.Shops.CountAsync();

        Console.WriteLine($"Created world '{world.Name}' (ruleset {world.RulesetVersion}, seed {world.Seed}) at {path}");
        Console.WriteLine($"  Settlements: {settlements}");
        Console.WriteLine($"  Goods:       {goods}");
        Console.WriteLine($"  Shops:       {shops}");
        return 0;
    }

    // ---- list <dbPath> ----
    private static async Task<int> CmdList(string[] args)
    {
        if (args.Length < 2)
            return MissingArgs("list <dbPath>");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        Console.WriteLine("(Money values are in minor currency units.)");
        Console.WriteLine();

        var settlements = await ctx.Settlements.ToListAsync();
        Console.WriteLine("Settlements:");
        foreach (var s in settlements.OrderBy(s => s.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {s.Name} | {s.Type} | pop {s.Population}");
        Console.WriteLine();

        var goods = await ctx.Goods.ToListAsync();
        Console.WriteLine("Goods:");
        foreach (var g in goods.OrderBy(g => g.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Name} | {g.Category} | baseValue {g.BaseValue.Units}");
        Console.WriteLine();

        var shops = await ctx.Shops.ToListAsync();
        var settlementById = settlements.ToDictionary(s => s.Id, s => s.Name);
        Console.WriteLine("Shops:");
        foreach (var sh in shops.OrderBy(sh => sh.Name, StringComparer.Ordinal))
        {
            var town = settlementById.TryGetValue(sh.SettlementId, out var n) ? n : "(unknown)";
            Console.WriteLine($"  {sh.Name} | {town}");
        }
        return 0;
    }

    // ---- price <dbPath> <settlementName> <goodName> ----
    private static async Task<int> CmdPrice(string[] args)
    {
        if (args.Length < 4)
            return MissingArgs("price <dbPath> <settlementName> <goodName>");

        var path = args[1];
        var settlementName = args[2];
        var goodName = args[3];

        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null)
        {
            Console.Error.WriteLine("Error: no world found in database. Run 'new' first.");
            return 1;
        }

        var settlement = (await ctx.Settlements.ToListAsync())
            .FirstOrDefault(s => string.Equals(s.Name, settlementName, StringComparison.OrdinalIgnoreCase));
        if (settlement is null)
        {
            Console.Error.WriteLine($"Error: settlement '{settlementName}' not found.");
            return 1;
        }

        var good = (await ctx.Goods.ToListAsync())
            .FirstOrDefault(g => string.Equals(g.Name, goodName, StringComparison.OrdinalIgnoreCase));
        if (good is null)
        {
            Console.Error.WriteLine($"Error: good '{goodName}' not found.");
            return 1;
        }

        var query = new PriceMarginQuery(new ShopRepository(ctx), new StockpileRepository(ctx), new GoodRepository(ctx));
        var result = await query.RunAsync(world.Id, settlement.Id, good.Id);
        if (result.IsError)
        {
            Console.Error.WriteLine($"Error: {string.Join("; ", result.Errors.Select(e => e.Description))}");
            return 1;
        }

        var value = result.Value;
        Console.WriteLine($"Prices for '{value.GoodName}' in {settlement.Name} (Money values are in minor currency units):");
        Console.WriteLine();

        if (value.Shops.Count == 0)
        {
            Console.WriteLine("  (no shop in this settlement stocks this good)");
            return 0;
        }

        Console.WriteLine($"  {"ShopName",-16} {"Stock",6} {"CostBasis",10} {"SalePrice",10} {"Margin(abs)",12} {"Margin(%)",10}");
        foreach (var line in value.Shops)
        {
            var marginPct = line.MarginBp / 100.0;
            Console.WriteLine(
                $"  {line.ShopName,-16} {line.Stock,6} {line.UnitCostBasis.Units,10} {line.SalePrice.Units,10} {line.MarginAbs.Units,12} {marginPct,9:0.##}%");
        }
        return 0;
    }

    // ---- advance <dbPath> <ticks> ----
    private static async Task<int> CmdAdvance(string[] args)
    {
        if (args.Length < 3)
            return MissingArgs("advance <dbPath> <ticks>");

        var path = args[1];
        if (!long.TryParse(args[2], out var ticks) || ticks < 0)
        {
            Console.Error.WriteLine($"Error: ticks must be a non-negative integer, got '{args[2]}'.");
            return 1;
        }

        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null)
        {
            Console.Error.WriteLine("Error: no world found in database. Run 'new' first.");
            return 1;
        }

        var sim = await SimulationContext.LoadAsync(ctx, world.Id);

        // No phases yet: this sub-phase only moves the clock.
        var engine = new TickEngine([]);
        await engine.AdvanceAsync(sim, ticks);

        var date = sim.Calendar.ToDate(sim.World.CurrentTick);
        Console.WriteLine($"Advanced {ticks} ticks.");
        Console.WriteLine($"  Current tick: {sim.World.CurrentTick.Value}");
        Console.WriteLine(
            $"  In-world date: Year {date.Year}, Month {date.Month}, Day {date.Day}, {date.Hour:D2}:{date.Minute:D2}");
        return 0;
    }

    // ---- snapshot <dbPath> <destPath> ----
    private static async Task<int> CmdSnapshot(string[] args)
    {
        if (args.Length < 3)
            return MissingArgs("snapshot <dbPath> <destPath>");

        var path = args[1];
        var dest = args[2];

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Error: source database '{path}' does not exist.");
            return 1;
        }

        var service = new SqliteSnapshotService();
        await service.CaptureAsync(path, dest);
        Console.WriteLine($"Snapshot written: {dest}");
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintUsage();
        return 1;
    }

    private static int MissingArgs(string usage)
    {
        Console.Error.WriteLine($"Error: missing arguments. Usage: {usage}");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("WorldEcon.Cli - headless world-economy harness");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  new      <dbPath>                          Create + migrate DB and seed the demo world.");
        Console.WriteLine("  list     <dbPath>                          List settlements, goods, and shops.");
        Console.WriteLine("  price    <dbPath> <settlement> <good>      Show shop prices/margins for a good in a settlement.");
        Console.WriteLine("  advance  <dbPath> <ticks>                  Advance in-world time by <ticks> minute-ticks.");
        Console.WriteLine("  snapshot <dbPath> <destPath>               Write a consistent snapshot copy of the DB.");
    }
}
