using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Actions;

namespace WorldEcon.Tui.Resources;

/// <summary>The append-only DM action audit log.</summary>
public sealed class ActionsResource : IResource
{
    public string Name => "actions";
    public IReadOnlyList<string> Aliases { get; } = ["log"];

    public async Task<ResourceTable> LoadAsync(TuiContext ctx)
    {
        var actions = (await ctx.Db.DmActions
                .Where(a => a.WorldId == ctx.World.Id)
                .ToListAsync())
            .OrderBy(a => a.Sequence)
            .ToList();

        var rows = actions
            .Select(a => new ResourceRow(a.Id.Value.ToString(),
            [
                a.Sequence.ToString(),
                a.AppliedTick.Value.ToString(),
                a.Kind.ToString(),
                a.Description,
            ]))
            .ToList();

        return new ResourceTable(["Seq", "Tick", "Kind", "Description"], rows);
    }

    public async Task<IReadOnlyList<string>> DetailsAsync(string key, TuiContext ctx)
    {
        var id = new DmActionId(Guid.Parse(key));
        var a = await ctx.Db.DmActions.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null)
            return [$"Action {key} not found."];

        return
        [
            $"Sequence: {a.Sequence}",
            $"AppliedTick: {a.AppliedTick.Value}",
            $"Kind: {a.Kind}",
            $"Description: {a.Description}",
            $"RecordedAtUtc: {a.RecordedAtUtc:O}",
            $"ArgsJson: {a.ArgsJson}",
            $"Id: {a.Id.Value}",
        ];
    }
}
