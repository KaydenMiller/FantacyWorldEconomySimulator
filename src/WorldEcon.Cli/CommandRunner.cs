using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine;
using WorldEcon.Engine.Actions;
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
                "stock" => await CmdStock(args),
                "merchants" => await CmdMerchants(args),
                "caravans" => await CmdCaravans(args),
                "snapshot" => await CmdSnapshot(args),
                "buy" => await CmdBuy(args),
                "adjust" => await CmdAdjust(args),
                "disable" => await CmdSetDisabled(args, true),
                "enable" => await CmdSetDisabled(args, false),
                "actions" => await CmdActions(args),
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

        var engine = new TickEngine(WorldEcon.Engine.StandardPhases.All());
        await engine.AdvanceAsync(sim, ticks);

        var date = sim.Calendar.ToDate(sim.World.CurrentTick);
        Console.WriteLine($"Advanced {ticks} ticks.");
        Console.WriteLine($"  Current tick: {sim.World.CurrentTick.Value}");
        Console.WriteLine(
            $"  In-world date: Year {date.Year}, Month {date.Month}, Day {date.Day}, {date.Hour:D2}:{date.Minute:D2}");
        return 0;
    }

    // ---- stock <dbPath> <settlementName> ----
    private static async Task<int> CmdStock(string[] args)
    {
        if (args.Length < 3)
            return MissingArgs("stock <dbPath> <settlementName>");

        var path = args[1];
        var settlementName = args[2];

        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        var settlement = (await ctx.Settlements.ToListAsync())
            .FirstOrDefault(s => string.Equals(s.Name, settlementName, StringComparison.OrdinalIgnoreCase));
        if (settlement is null)
        {
            Console.Error.WriteLine($"Error: settlement '{settlementName}' not found.");
            return 1;
        }

        var goodsById = (await ctx.Goods.ToListAsync()).ToDictionary(g => g.Id, g => g.Name);

        var stockpiles = (await ctx.Stockpiles
                .Where(s => s.OwnerKind == WorldEcon.Domain.Economy.StockpileOwnerKind.SettlementMarket
                            && s.OwnerId == settlement.Id.Value)
                .ToListAsync())
            .OrderBy(s => goodsById.TryGetValue(s.GoodId, out var n) ? n : string.Empty, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"Market stockpiles in {settlement.Name} (Money values are in minor currency units):");
        Console.WriteLine();

        if (stockpiles.Count == 0)
        {
            Console.WriteLine("  (no market stockpiles)");
            return 0;
        }

        Console.WriteLine($"  {"Good",-16} {"Quantity",10} {"CostBasis",10} {"MarketPrice",12}");
        foreach (var sp in stockpiles)
        {
            var name = goodsById.TryGetValue(sp.GoodId, out var n) ? n : "(unknown)";
            Console.WriteLine($"  {name,-16} {sp.Quantity,10} {sp.CostBasis.Units,10} {sp.MarketPrice.Units,12}");
        }
        return 0;
    }

    // ---- merchants <dbPath> ----
    private static async Task<int> CmdMerchants(string[] args)
    {
        if (args.Length < 2)
            return MissingArgs("merchants <dbPath>");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        var settlementById = (await ctx.Settlements.ToListAsync()).ToDictionary(s => s.Id, s => s.Name);

        var merchants = (await ctx.Merchants.ToListAsync())
            .OrderBy(m => settlementById.TryGetValue(m.Seat, out var n) ? n : string.Empty, StringComparer.Ordinal)
            .ThenBy(m => m.Id.Value)
            .ToList();

        Console.WriteLine("Merchants (Money values are in minor currency units):");
        Console.WriteLine();

        if (merchants.Count == 0)
        {
            Console.WriteLine("  (no merchants)");
            return 0;
        }

        Console.WriteLine($"  {"Seat",-16} {"Capital",12} {"CargoCapacity",14} {"Reach",8}");
        foreach (var m in merchants)
        {
            var seat = settlementById.TryGetValue(m.Seat, out var n) ? n : "(unknown)";
            Console.WriteLine($"  {seat,-16} {m.Capital.Units,12} {m.CargoCapacity,14} {m.Reach,8}");
        }
        return 0;
    }

    // ---- caravans <dbPath> ----
    private static async Task<int> CmdCaravans(string[] args)
    {
        if (args.Length < 2)
            return MissingArgs("caravans <dbPath>");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        var settlementById = (await ctx.Settlements.ToListAsync()).ToDictionary(s => s.Id, s => s.Name);
        var goodById = (await ctx.Goods.ToListAsync()).ToDictionary(g => g.Id, g => g.Name);

        var caravans = (await ctx.Caravans.ToListAsync())
            .OrderBy(c => c.ArriveTick.Value)
            .ThenBy(c => c.Id.Value)
            .ToList();

        Console.WriteLine("Caravans (Money values are in minor currency units):");
        Console.WriteLine();

        if (caravans.Count == 0)
        {
            Console.WriteLine("  (no caravans)");
            return 0;
        }

        Console.WriteLine($"  {"Origin",-12} {"Dest",-12} {"Good",-14} {"Qty",6} {"UnitCost",9} {"Depart",10} {"Arrive",10} {"Delivered",10}");
        foreach (var c in caravans)
        {
            var origin = settlementById.TryGetValue(c.OriginId, out var o) ? o : "(unknown)";
            var dest = settlementById.TryGetValue(c.DestinationId, out var d) ? d : "(unknown)";
            var good = goodById.TryGetValue(c.GoodId, out var g) ? g : "(unknown)";
            var delivered = c.Delivered ? "yes" : "no";
            Console.WriteLine(
                $"  {origin,-12} {dest,-12} {good,-14} {c.Quantity,6} {c.UnitCostBasis.Units,9} {c.DepartTick.Value,10} {c.ArriveTick.Value,10} {delivered,10}");
        }
        return 0;
    }

    // ---- buy <dbPath> <settlementName> <goodName> <qty> ----
    private static async Task<int> CmdBuy(string[] args)
    {
        if (args.Length < 5)
            return MissingArgs("buy <dbPath> <settlement> <good> <qty>");

        if (!long.TryParse(args[4], out var qty) || qty < 1)
        {
            Console.Error.WriteLine($"Error: qty must be a positive integer, got '{args[4]}'.");
            return 1;
        }

        await using var ctx = OpenContext(args[1]);
        ctx.Database.Migrate();

        var (world, settlement, good, fail) = await ResolveWorldSettlementGood(ctx, args[2], args[3]);
        if (fail != 0) return fail;

        var service = new DmActionService(ctx);
        var result = await service.BuyFromShopsAsync(world!.Id, settlement!.Id, good!.Id, qty, DateTimeOffset.UtcNow);
        return PrintActionResult(result);
    }

    // ---- adjust <dbPath> <settlementName> <goodName> <delta> ----
    private static async Task<int> CmdAdjust(string[] args)
    {
        if (args.Length < 5)
            return MissingArgs("adjust <dbPath> <settlement> <good> <delta>");

        if (!long.TryParse(args[4], out var delta) || delta == 0)
        {
            Console.Error.WriteLine($"Error: delta must be a non-zero integer, got '{args[4]}'.");
            return 1;
        }

        await using var ctx = OpenContext(args[1]);
        ctx.Database.Migrate();

        var (world, settlement, good, fail) = await ResolveWorldSettlementGood(ctx, args[2], args[3]);
        if (fail != 0) return fail;

        var service = new DmActionService(ctx);
        var result = await service.AdjustMarketStockAsync(world!.Id, settlement!.Id, good!.Id, delta, DateTimeOffset.UtcNow);
        return PrintActionResult(result);
    }

    // ---- disable|enable <dbPath> <settlementName> ----
    private static async Task<int> CmdSetDisabled(string[] args, bool disabled)
    {
        var verb = disabled ? "disable" : "enable";
        if (args.Length < 3)
            return MissingArgs($"{verb} <dbPath> <settlement>");

        await using var ctx = OpenContext(args[1]);
        ctx.Database.Migrate();

        var (world, settlement, fail) = await ResolveWorldSettlement(ctx, args[2]);
        if (fail != 0) return fail;

        var service = new DmActionService(ctx);
        var result = await service.SetSettlementProductionDisabledAsync(world!.Id, settlement!.Id, disabled, DateTimeOffset.UtcNow);
        return PrintActionResult(result);
    }

    // ---- actions <dbPath> ----
    private static async Task<int> CmdActions(string[] args)
    {
        if (args.Length < 2)
            return MissingArgs("actions <dbPath>");

        await using var ctx = OpenContext(args[1]);
        ctx.Database.Migrate();

        var actions = (await ctx.DmActions.ToListAsync())
            .OrderBy(a => a.Sequence)
            .ToList();

        Console.WriteLine("DM action log:");
        Console.WriteLine();

        if (actions.Count == 0)
        {
            Console.WriteLine("  (no actions logged)");
            return 0;
        }

        foreach (var a in actions)
            Console.WriteLine($"  #{a.Sequence} @tick{a.AppliedTick.Value} {a.Kind} — {a.Description}");
        return 0;
    }

    private static async Task<(World? World, Settlement? Settlement, int Fail)> ResolveWorldSettlement(
        WorldDbContext ctx, string settlementName)
    {
        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null)
        {
            Console.Error.WriteLine("Error: no world found in database. Run 'new' first.");
            return (null, null, 1);
        }

        var settlement = (await ctx.Settlements.ToListAsync())
            .FirstOrDefault(s => string.Equals(s.Name, settlementName, StringComparison.OrdinalIgnoreCase));
        if (settlement is null)
        {
            Console.Error.WriteLine($"Error: settlement '{settlementName}' not found.");
            return (world, null, 1);
        }

        return (world, settlement, 0);
    }

    private static async Task<(World? World, Settlement? Settlement, Good? Good, int Fail)> ResolveWorldSettlementGood(
        WorldDbContext ctx, string settlementName, string goodName)
    {
        var (world, settlement, fail) = await ResolveWorldSettlement(ctx, settlementName);
        if (fail != 0)
            return (world, settlement, null, fail);

        var good = (await ctx.Goods.ToListAsync())
            .FirstOrDefault(g => string.Equals(g.Name, goodName, StringComparison.OrdinalIgnoreCase));
        if (good is null)
        {
            Console.Error.WriteLine($"Error: good '{goodName}' not found.");
            return (world, settlement, null, 1);
        }

        return (world, settlement, good, 0);
    }

    private static int PrintActionResult(ErrorOr.ErrorOr<WorldEcon.Domain.Actions.DmAction> result)
    {
        if (result.IsError)
        {
            Console.Error.WriteLine($"Error: {string.Join("; ", result.Errors.Select(e => e.Description))}");
            return 1;
        }

        Console.WriteLine(result.Value.Description);
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
        Console.WriteLine("  stock    <dbPath> <settlement>             Show a settlement's market stockpiles.");
        Console.WriteLine("  merchants <dbPath>                         List representative merchants and their seats.");
        Console.WriteLine("  caravans <dbPath>                          List caravans (in-transit and delivered).");
        Console.WriteLine("  snapshot <dbPath> <destPath>               Write a consistent snapshot copy of the DB.");
        Console.WriteLine("  buy      <dbPath> <settlement> <good> <qty> Party buys a good off shop shelves.");
        Console.WriteLine("  adjust   <dbPath> <settlement> <good> <delta> Party adjusts market stock (delta may be negative).");
        Console.WriteLine("  disable  <dbPath> <settlement>             Party disables all production in a settlement.");
        Console.WriteLine("  enable   <dbPath> <settlement>             Party restores all production in a settlement.");
        Console.WriteLine("  actions  <dbPath>                          List the DM/party action log.");
    }
}
