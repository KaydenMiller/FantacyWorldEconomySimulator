using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Routes;
using WorldEcon.Persistence;

namespace WorldEcon.Cli;

/// <summary>The <c>node rename</c> command: names a junction or site, or clears its name.</summary>
internal static class NodeCommands
{
    public const string Usage = "node rename <dbPath> <nodeId> <name|--clear>";

    /// <summary><c>node rename &lt;dbPath&gt; &lt;nodeId&gt; &lt;name|--clear&gt;</c>: nodeId is the full Guid or a unique prefix.</summary>
    public static async Task<int> RunAsync(string[] args, Func<string, WorldDbContext> open, Func<string, int> missingArgs)
    {
        if (args.Length < 5 || !args[1].Equals("rename", StringComparison.OrdinalIgnoreCase))
            return missingArgs(Usage);
        if (args.Length > 5)
        {
            // An unquoted name arrives as several arguments; saving only the first word would be a silent surprise.
            Console.Error.WriteLine($"Error: too many arguments; quote a name with spaces. Usage: {Usage}");
            return 1;
        }
        var name = args[4] == "--clear" ? null : args[4];
        await using var ctx = open(args[2]);
        ctx.Database.Migrate();
        var result = await RouteNodeRenameService.RenameAsync(ctx, args[3], name);
        if (result.IsError)
        {
            Console.Error.WriteLine($"Error: {result.FirstError.Description}");
            return 1;
        }

        var n = result.Value;
        Console.WriteLine(n.Name is null
            ? $"Cleared the name of {n.Kind} @ tile {n.TileId}"
            : $"Renamed {n.Kind} @ tile {n.TileId} to \"{n.Name}\"");
        return 0;
    }
}
