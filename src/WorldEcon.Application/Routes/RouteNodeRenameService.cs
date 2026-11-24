using ErrorOr;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Globe;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;

namespace WorldEcon.Application.Routes;

/// <summary>A junction or site after <see cref="RouteNodeRenameService.RenameAsync(string, string, string?, CancellationToken)"/>.</summary>
public sealed record RenamedNode(Guid Id, RouteNodeKind Kind, int TileId, string? Name);

/// <summary>
/// Names a junction or site, or clears its name (null), and saves it. The node is given by its full id or a unique prefix
/// (case-insensitive, with or without hyphens). Used by the CLI <c>node rename</c> command and by the desktop app.
/// </summary>
public static class RouteNodeRenameService
{
    /// <summary>Opens <paramref name="dbPath"/> (which must exist), renames and saves.</summary>
    public static async Task<ErrorOr<RenamedNode>> RenameAsync(string dbPath, string idOrPrefix, string? name, CancellationToken ct = default)
    {
        await using var ctx = GlobeWorldLoader.Open(dbPath);
        return await RenameAsync(ctx, idOrPrefix, name, ct);
    }

    /// <summary>Renames in <paramref name="ctx"/> and saves its changes.</summary>
    public static async Task<ErrorOr<RenamedNode>> RenameAsync(WorldDbContext ctx, string idOrPrefix, string? name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(idOrPrefix);
        var found = await ResolveAsync(ctx, idOrPrefix, ct);
        if (found.IsError) return found.Errors;
        var node = await ctx.RouteNodes.SingleAsync(n => n.Id == found.Value, ct);
        var renamed = node.Rename(name);
        if (renamed.IsError) return renamed.Errors;
        await ctx.SaveChangesAsync(ct);
        return new RenamedNode(node.Id.Value, node.Kind, node.TileId, node.Name);
    }

    /// <summary>The one node whose id starts with <paramref name="idOrPrefix"/> (hyphens and case ignored).</summary>
    public static async Task<ErrorOr<RouteNodeId>> ResolveAsync(WorldDbContext ctx, string idOrPrefix, CancellationToken ct = default)
    {
        var prefix = idOrPrefix.Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        var matches = (await ctx.RouteNodes.AsNoTracking().Select(n => n.Id).ToListAsync(ct))
            .Where(id => id.Value.ToString("N").StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(id => id.Value).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => Error.NotFound("routenode.notFound", $"No route node id starts with '{idOrPrefix}'."),
            _ => Error.Conflict("routenode.ambiguous",
                $"'{idOrPrefix}' matches {matches.Count} route nodes ({string.Join(", ", matches.Take(5).Select(m => m.Value.ToString()))}{(matches.Count > 5 ? ", …" : "")}); give more of the id."),
        };
    }
}
