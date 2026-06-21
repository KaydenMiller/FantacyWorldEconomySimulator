using Microsoft.EntityFrameworkCore;
using Terminal.Gui.App;
using WorldEcon.Persistence;
using WorldEcon.Tui;
using WorldEcon.Tui.Shell;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var dbPath = args[0];
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"Error: world database '{dbPath}' does not exist.");
    Console.Error.WriteLine("The TUI opens an existing world. Create one with the CLI: WorldEcon.Cli new <dbPath>");
    return 1;
}

await using var db = new WorldDbContext(
    new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={dbPath}").Options);
db.Database.Migrate();

TuiContext ctx;
try
{
    ctx = await TuiContext.LoadAsync(db, dbPath);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

var registry = CommandRegistry.CreateDefault();

var app = Application.Create();
app.Init();
try
{
    using var shell = new TuiShell(ctx, registry, app: app);
    shell.Ui = new ShellUserInteraction(shell, app);
    app.Run(shell, null);
}
finally
{
    app.Dispose();
}

return 0;

static void PrintUsage()
{
    Console.WriteLine("WorldEcon.Tui - k9s-style terminal UI for an existing world database");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  WorldEcon.Tui <dbPath>    Open an existing world database in the TUI.");
    Console.WriteLine();
    Console.WriteLine("Create a world first with the CLI: WorldEcon.Cli new <dbPath>");
}
