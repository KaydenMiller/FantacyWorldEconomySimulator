using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Logging;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine;
using WorldEcon.Engine.Actions;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Repositories;
using WorldEcon.SharedKernel;
using WorldEcon.Persistence.Snapshots;
using WorldEcon.Seeding;
using WorldEcon.SharedKernel.Currency;
using WorldEcon.SharedKernel.Measure;
using WorldEcon.Simulation.Time;

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
                "import" => await CmdImport(args),
                "list" => await CmdList(args),
                "price" => await CmdPrice(args),
                "advance" => await CmdAdvance(args),
                "stock" => await CmdStock(args),
                "merchants" => await CmdMerchants(args),
                "consumers" => await CmdConsumers(args),
                "money" or "ledger" => await CmdMoney(args),
                "caravans" => await CmdCaravans(args),
                "snapshot" => await CmdSnapshot(args),
                "buy" => await CmdBuy(args),
                "adjust" => await CmdAdjust(args),
                "disable" => await CmdSetDisabled(args, true),
                "enable" => await CmdSetDisabled(args, false),
                "actions" => await CmdActions(args),
                "log" => await CmdLog(args),
                "summary" => await CmdSummary(args),
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

    // ---- import <jsonPath> <dbPath> ----
    private static async Task<int> CmdImport(string[] args)
    {
        if (args.Length < 3)
            return MissingArgs("import <jsonPath> <dbPath>");

        var jsonPath = args[1];
        var dbPath = args[2];

        await using var ctx = OpenContext(dbPath);
        ctx.Database.Migrate();

        var seed = await new JsonSeedSource(jsonPath).LoadAsync();
        var worldId = await new SeedImporter(ctx).ImportAsync(seed);

        var world = await ctx.Worlds.FirstAsync(w => w.Id == worldId);
        var settlements = await ctx.Settlements.CountAsync();
        var goods = await ctx.Goods.CountAsync();
        var merchants = await ctx.Merchants.CountAsync();

        Console.WriteLine($"Imported world '{world.Name}' (ruleset {world.RulesetVersion}, seed {world.Seed}) from {jsonPath} into {dbPath}");
        Console.WriteLine($"  Settlements: {settlements}");
        Console.WriteLine($"  Goods:       {goods}");
        Console.WriteLine($"  Merchants:   {merchants}");
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

        var listWorld = await ctx.Worlds.FirstOrDefaultAsync();
        var listCurrency = listWorld?.Currency ?? CurrencyDefinition.Default;

        Console.WriteLine();

        var settlements = await ctx.Settlements.ToListAsync();
        Console.WriteLine("Settlements:");
        foreach (var s in settlements.OrderBy(s => s.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {s.Name} | {s.Type} | pop {s.Population}");
        Console.WriteLine();

        var goods = await ctx.Goods.ToListAsync();
        var listUnits = listWorld?.DisplayUnitSystem ?? UnitSystem.Metric;
        Console.WriteLine("Goods:");
        foreach (var g in goods.OrderBy(g => g.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Name} | {g.Category} | baseValue {listCurrency.Format(g.BaseValue)} | {MeasurementFormat.FormatMass(g.MassPerUnit, listUnits)} | {MeasurementFormat.FormatVolume(g.VolumePerUnit, listUnits)}");
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
        var currency = world.Currency;
        Console.WriteLine($"Prices for '{value.GoodName}' in {settlement.Name}:");
        Console.WriteLine();

        if (value.Shops.Count == 0)
        {
            Console.WriteLine("  (no shop in this settlement stocks this good)");
            return 0;
        }

        // "Retail" is the price discovery auction's last clearing price — what townsfolk actually paid.
        // Margin is realized against cost basis (Retail - CostBasis), not the shop's nominal markup.
        Console.WriteLine($"  {"ShopName",-16} {"Stock",6} {"Retail",12} {"CostBasis",12} {"Margin(abs)",14} {"Margin(%)",10}");
        foreach (var line in value.Shops)
        {
            var realizedMargin = line.RetailPrice.Units - line.UnitCostBasis.Units;
            var marginPct = line.UnitCostBasis.Units > 0 ? realizedMargin * 100.0 / line.UnitCostBasis.Units : 0.0;
            Console.WriteLine(
                $"  {line.ShopName,-16} {line.Stock,6} {currency.Format(line.RetailPrice),12} {currency.Format(line.UnitCostBasis),12} {currency.Format(new Money(realizedMargin)),14} {marginPct,9:0.##}%");
        }
        return 0;
    }

    // ---- advance <dbPath> <duration> ----
    // <duration> is either a bare integer (raw ticks/minutes) or <n><unit> where unit is:
    //   m=minute, h=hour, d=day, w=week, M=month, y=year  (case-sensitive: m≠M)
    private static async Task<int> CmdAdvance(string[] args)
    {
        if (args.Length < 3)
            return MissingArgs("advance <dbPath> <duration>  (e.g. 1440, 1d, 2w, 1M — m=minute, M=month)");

        var path = args[1];

        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null)
        {
            Console.Error.WriteLine("Error: no world found in database. Run 'new' first.");
            return 1;
        }

        var calendar = new CalendarSystem(world.Calendar);
        if (!calendar.TryParseDurationToTicks(args[2], out var ticks))
        {
            Console.Error.WriteLine(
                $"Error: invalid duration '{args[2]}'. Use ticks or <n>m/h/d/w/M/y (m=minute, M=month).");
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
        var stockWorld = await ctx.Worlds.FirstOrDefaultAsync();
        var stockCurrency = stockWorld?.Currency ?? CurrencyDefinition.Default;

        var shopIds = (await ctx.Shops.Where(sh => sh.WorldId == settlement.WorldId && sh.SettlementId == settlement.Id).ToListAsync())
            .Select(sh => sh.Id.Value).ToHashSet();
        var stockGroups = (await ctx.Stockpiles
                .Where(s => s.OwnerKind == WorldEcon.Domain.Economy.StockpileOwnerKind.Shop)
                .ToListAsync())
            .Where(s => shopIds.Contains(s.OwnerId))
            .GroupBy(s => s.GoodId)
            .Select(g => new
            {
                GoodId = g.Key,
                Qty = g.Sum(x => x.Quantity),
                MinCost = g.Min(x => x.CostBasis.Units),
                Price = g.Select(x => x.MarketPrice.Units).FirstOrDefault(p => p > 0)
            })
            .OrderBy(x => goodsById.TryGetValue(x.GoodId, out var n) ? n : string.Empty, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"Shop stock in {settlement.Name}:");
        Console.WriteLine();

        if (stockGroups.Count == 0)
        {
            Console.WriteLine("  (no shop stockpiles)");
            return 0;
        }

        Console.WriteLine($"  {"Good",-16} {"Quantity",10} {"Min Price",12} {"Price",14}");
        foreach (var sg in stockGroups)
        {
            var name = goodsById.TryGetValue(sg.GoodId, out var n) ? n : "(unknown)";
            Console.WriteLine($"  {name,-16} {sg.Qty,10} {stockCurrency.Format(new WorldEcon.SharedKernel.Money(sg.MinCost)),12} {stockCurrency.Format(new WorldEcon.SharedKernel.Money(sg.Price)),14}");
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

        var world = await ctx.Worlds.FirstOrDefaultAsync();
        var currency = world?.Currency ?? CurrencyDefinition.Default;

        Console.WriteLine("Merchants:");
        Console.WriteLine();

        if (merchants.Count == 0)
        {
            Console.WriteLine("  (no merchants)");
            return 0;
        }

        var units = world?.DisplayUnitSystem ?? UnitSystem.Metric;
        Console.WriteLine($"  {"Seat",-16} {"Capital",12} {"Weight cap",12} {"Volume cap",12} {"Reach",8}");
        foreach (var m in merchants)
        {
            var seat = settlementById.TryGetValue(m.Seat, out var n) ? n : "(unknown)";
            Console.WriteLine($"  {seat,-16} {currency.Format(m.Capital),12} {MeasurementFormat.FormatMass(m.WeightCapacity, units),12} {MeasurementFormat.FormatVolume(m.VolumeCapacity, units),12} {m.Reach,8}");
        }
        return 0;
    }

    // ---- consumers <dbPath> ----
    private static async Task<int> CmdConsumers(string[] args)
    {
        if (args.Length < 2)
            return MissingArgs("consumers <dbPath>");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        var settlementById = (await ctx.Settlements.ToListAsync()).ToDictionary(s => s.Id, s => s.Name);

        var consumers = (await ctx.Consumers.ToListAsync())
            .OrderBy(c => settlementById.TryGetValue(c.Seat, out var n) ? n : string.Empty, StringComparer.Ordinal)
            .ThenBy(c => c.Id.Value)
            .ToList();

        var world = await ctx.Worlds.FirstOrDefaultAsync();
        var currency = world?.Currency ?? CurrencyDefinition.Default;

        Console.WriteLine("Consumers:");
        Console.WriteLine();

        if (consumers.Count == 0)
        {
            Console.WriteLine("  (no consumers — advance at least 1 week to spawn them)");
            return 0;
        }

        Console.WriteLine($"  {"Seat",-16} {"Size",10} {"Budget",14}");
        foreach (var c in consumers)
        {
            var seat = settlementById.TryGetValue(c.Seat, out var n) ? n : "(unknown)";
            Console.WriteLine($"  {seat,-16} {c.Size,10} {currency.Format(c.Budget),14}");
        }
        return 0;
    }

    // ---- money <dbPath> ----  (money-supply ledger)
    private static async Task<int> CmdMoney(string[] args)
    {
        if (args.Length < 2)
            return MissingArgs("money <dbPath>");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();

        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null)
        {
            Console.Error.WriteLine("Error: no world found. Run 'new' first.");
            return 1;
        }
        var currency = world.Currency;
        var calendar = new CalendarSystem(world.Calendar);

        var snapshots = (await ctx.MoneyLedgerSnapshots.Where(s => s.WorldId == world.Id).ToListAsync())
            .OrderBy(s => s.Sequence).ToList();
        if (snapshots.Count == 0)
        {
            Console.WriteLine("Money ledger: (no snapshots yet — advance at least one in-world month)");
            return 0;
        }

        var latest = snapshots[^1];
        var lines = (await ctx.MoneyLedgerLines.Where(l => l.SnapshotId == latest.Id).ToListAsync())
            .OrderBy(l => (int)l.Kind).ThenBy(l => (int)l.Channel).ToList();
        var d = calendar.ToDate(latest.Tick);

        Console.WriteLine($"Money supply @ Y{d.Year} M{d.Month} D{d.Day} (tick {latest.Tick.Value}):");
        Console.WriteLine($"  Total in circulation: {currency.Format(latest.TotalSupply)}");
        Console.WriteLine($"  Net change this month: {currency.Format(latest.NetDelta)}  (faucets − sinks)");
        if (latest.Discrepancy.Units != 0)
            Console.WriteLine($"  ⚠ Discrepancy: {currency.Format(latest.Discrepancy)}  (untracked flow — should be 0)");
        Console.WriteLine();
        Console.WriteLine($"  This month's flows by channel:");
        if (lines.Count == 0)
            Console.WriteLine("    (no flows)");
        foreach (var l in lines)
            Console.WriteLine($"    {l.Kind,-9} {l.Channel,-18} {currency.Format(l.Amount),14}");

        Console.WriteLine();
        Console.WriteLine("  History (recent months):");
        Console.WriteLine($"    {"Date",-14} {"Supply",16} {"Net Δ",14}");
        foreach (var s in snapshots.TakeLast(12))
        {
            var sd = calendar.ToDate(s.Tick);
            Console.WriteLine($"    {$"Y{sd.Year} M{sd.Month} D{sd.Day}",-14} {currency.Format(s.TotalSupply),16} {currency.Format(s.NetDelta),14}");
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
        var caravanWorld = await ctx.Worlds.FirstOrDefaultAsync();
        var caravanCurrency = caravanWorld?.Currency ?? CurrencyDefinition.Default;

        var caravans = (await ctx.Caravans.ToListAsync())
            .OrderBy(c => c.ArriveTick.Value)
            .ThenBy(c => c.Id.Value)
            .ToList();

        Console.WriteLine("Caravans:");
        Console.WriteLine();

        if (caravans.Count == 0)
        {
            Console.WriteLine("  (no caravans)");
            return 0;
        }

        Console.WriteLine($"  {"Origin",-12} {"Dest",-12} {"Good",-14} {"Qty",6} {"UnitCost",12} {"Depart",10} {"Arrive",10} {"Delivered",10}");
        foreach (var c in caravans)
        {
            var origin = settlementById.TryGetValue(c.OriginId, out var o) ? o : "(unknown)";
            var dest = settlementById.TryGetValue(c.DestinationId, out var d) ? d : "(unknown)";
            var good = goodById.TryGetValue(c.GoodId, out var g) ? g : "(unknown)";
            var delivered = c.Delivered ? "yes" : "no";
            Console.WriteLine(
                $"  {origin,-12} {dest,-12} {good,-14} {c.Quantity,6} {caravanCurrency.Format(c.UnitCostBasis),12} {c.DepartTick.Value,10} {c.ArriveTick.Value,10} {delivered,10}");
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

        var service = new LogEventService(ctx);
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

        var service = new LogEventService(ctx);
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

        var service = new LogEventService(ctx);
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

        var events = (await ctx.LogEvents.Where(e => e.IsPlayerAction).ToListAsync())
            .OrderBy(e => e.Sequence)
            .ToList();
        Console.WriteLine("Party/DM actions:");
        Console.WriteLine();
        if (events.Count == 0) { Console.WriteLine("  (none)"); return 0; }
        foreach (var e in events)
            Console.WriteLine($"  #{e.Sequence,-4} tick {e.OccurredTick.Value,-8} {e.Message}");
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

    private static int PrintActionResult(ErrorOr.ErrorOr<WorldEcon.Domain.Logging.LogEvent> result)
    {
        if (result.IsError)
        {
            Console.Error.WriteLine($"Error: {string.Join("; ", result.Errors.Select(e => e.Description))}");
            return 1;
        }

        Console.WriteLine(result.Value.Message);
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

    // ---- log <dbPath> <kind> <name> [--regex <pattern>] [--limit <n>] ----
    private static async Task<int> CmdLog(string[] args)
    {
        if (args.Length < 4)
            return MissingArgs("log <dbPath> <world|continent|country|region|city> <name> [--regex <p>] [--limit <n>]");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();
        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null) { Console.Error.WriteLine("Error: no world found."); return 1; }

        var (kind, id, knownKind) = await ResolveScope(ctx, world.Id, args[2], args[3]);
        if (!knownKind) { Console.Error.WriteLine($"Error: unknown scope kind '{args[2]}'. Expected: world|continent|country|region|city."); return 1; }
        if (id is null) { Console.Error.WriteLine($"Error: {args[2]} '{args[3]}' not found."); return 1; }

        string? regex = OptValue(args, "--regex");
        int limit = int.TryParse(OptValue(args, "--limit"), out var n) ? n : 50;
        if (limit < 1) { Console.Error.WriteLine("Error: --limit must be a positive integer."); return 1; }

        var events = await new LogQueryService(ctx).QueryAsync(world.Id, kind, id.Value, regex, limit);
        Console.WriteLine($"Log for {args[2]} '{args[3]}' (newest first):");
        Console.WriteLine();
        if (events.Count == 0) { Console.WriteLine("  (no events)"); return 0; }
        foreach (var e in events)
            Console.WriteLine($"  tick {e.OccurredTick.Value,-8} {e.Magnitude,-8} {e.Type,-18} {e.Message}");
        return 0;
    }

    // ---- summary <dbPath> <kind> <name> [--from <tick>] [--to <tick>] ----
    private static async Task<int> CmdSummary(string[] args)
    {
        if (args.Length < 4)
            return MissingArgs("summary <dbPath> <world|continent|country|region|city> <name> [--from <tick>] [--to <tick>]");

        var path = args[1];
        await using var ctx = OpenContext(path);
        ctx.Database.Migrate();
        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null) { Console.Error.WriteLine("Error: no world found."); return 1; }

        var (kind, id, knownKind) = await ResolveScope(ctx, world.Id, args[2], args[3]);
        if (!knownKind) { Console.Error.WriteLine($"Error: unknown scope kind '{args[2]}'. Expected: world|continent|country|region|city."); return 1; }
        if (id is null) { Console.Error.WriteLine($"Error: {args[2]} '{args[3]}' not found."); return 1; }

        long from = long.TryParse(OptValue(args, "--from"), out var f) ? f : 0;
        long to = long.TryParse(OptValue(args, "--to"), out var t) ? t : world.CurrentTick.Value;
        if (from > to) { Console.Error.WriteLine($"Error: --from ({from}) must not be greater than --to ({to})."); return 1; }

        var sum = await new SummaryService(ctx).SummarizeAsync(world.Id, kind, id.Value,
            new WorldEcon.SharedKernel.Tick(from), new WorldEcon.SharedKernel.Tick(to));

        Console.WriteLine($"Summary for {args[2]} '{args[3]}' over ticks {from}..{to}:");
        Console.WriteLine($"  total events: {sum.TotalEvents}");
        foreach (var kv in sum.CountByType.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
            Console.WriteLine($"    {kv.Key,-18} {kv.Value}");
        if (sum.MajorEvents.Count > 0)
        {
            Console.WriteLine("  notable:");
            foreach (var e in sum.MajorEvents)
                Console.WriteLine($"    tick {e.OccurredTick.Value,-8} {e.Type,-18} {e.Message}");
        }
        return 0;
    }

    private static string? OptValue(string[] args, string flag, int startIndex = 4)
    {
        for (int i = startIndex; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static async Task<(LogScopeKind Kind, Guid? Id, bool KnownKind)> ResolveScope(
        WorldDbContext ctx, WorldId worldId, string kindToken, string name)
    {
        switch (kindToken.ToLowerInvariant())
        {
            case "world":
                return (LogScopeKind.World, worldId.Value, true);
            case "continent":
                return (LogScopeKind.Continent,
                    (await ctx.Continents.Where(x => x.WorldId == worldId).ToListAsync()).FirstOrDefault(x => Eq(x.Name, name))?.Id.Value,
                    true);
            case "country":
                return (LogScopeKind.Country,
                    (await ctx.Countries.Where(x => x.WorldId == worldId).ToListAsync()).FirstOrDefault(x => Eq(x.Name, name))?.Id.Value,
                    true);
            case "region":
                return (LogScopeKind.Region,
                    (await ctx.Regions.Where(x => x.WorldId == worldId).ToListAsync()).FirstOrDefault(x => Eq(x.Name, name))?.Id.Value,
                    true);
            case "city":
            case "settlement":
                return (LogScopeKind.Settlement,
                    (await ctx.Settlements.Where(x => x.WorldId == worldId).ToListAsync()).FirstOrDefault(x => Eq(x.Name, name))?.Id.Value,
                    true);
            default:
                return (LogScopeKind.World, null, false);
        }

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
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
        Console.WriteLine("  import   <jsonPath> <dbPath>               Create + migrate DB and import a JSON world seed.");
        Console.WriteLine("  list     <dbPath>                          List settlements, goods, and shops.");
        Console.WriteLine("  price    <dbPath> <settlement> <good>      Show shop prices/margins for a good in a settlement.");
        Console.WriteLine("  advance  <dbPath> <duration>               Advance in-world time. <duration> is a bare integer (ticks/minutes)");
        Console.WriteLine("                                             or <n><unit> where unit: m=minute h=hour d=day w=week M=month y=year.");
        Console.WriteLine("  stock    <dbPath> <settlement>             Show a settlement's market stockpiles.");
        Console.WriteLine("  merchants <dbPath>                         List representative merchants and their seats.");
        Console.WriteLine("  consumers <dbPath>                         List representative consumers (seat, size, budget).");
        Console.WriteLine("  money    <dbPath>                          Money-supply ledger: total in circulation, faucet/sink/transfer flows, history.");
        Console.WriteLine("  caravans <dbPath>                          List caravans (in-transit and delivered).");
        Console.WriteLine("  snapshot <dbPath> <destPath>               Write a consistent snapshot copy of the DB.");
        Console.WriteLine("  buy      <dbPath> <settlement> <good> <qty> Party buys a good off shop shelves.");
        Console.WriteLine("  adjust   <dbPath> <settlement> <good> <delta> Party adjusts market stock (delta may be negative).");
        Console.WriteLine("  disable  <dbPath> <settlement>             Party disables all production in a settlement.");
        Console.WriteLine("  enable   <dbPath> <settlement>             Party restores all production in a settlement.");
        Console.WriteLine("  actions  <dbPath>                          List the DM/party action log.");
        Console.WriteLine("  log      <dbPath> <kind> <name>            Show the activity log for a scope (world|continent|country|region|city).");
        Console.WriteLine("           [--regex <p>] [--limit <n>]       Optional regex filter and page limit (default 50).");
        Console.WriteLine("  summary  <dbPath> <kind> <name>            Show event counts by type for a scope.");
        Console.WriteLine("           [--from <tick>] [--to <tick>]     Optional tick window (default: full history).");
    }
}
