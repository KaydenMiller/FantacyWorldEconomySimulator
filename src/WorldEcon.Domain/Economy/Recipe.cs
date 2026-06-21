using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>A single input or output line of a <see cref="Recipe"/>.</summary>
public sealed record RecipeLine(GoodId Good, long Quantity, RecipeLineKind Kind);

public sealed class Recipe : AggregateRoot<RecipeId>
{
    public WorldId WorldId { get; }
    public string Name { get; private set; }
    public FacilityType Facility { get; private set; }
    public IReadOnlyList<RecipeLine> Lines { get; private set; }
    public long LaborCost { get; private set; }
    public long TicksToProduce { get; private set; }

    public IEnumerable<RecipeLine> Inputs => Lines.Where(l => l.Kind == RecipeLineKind.Input);
    public IEnumerable<RecipeLine> Outputs => Lines.Where(l => l.Kind == RecipeLineKind.Output);

    private Recipe() : base(default) { Name = null!; Lines = null!; } // EF

    private Recipe(RecipeId id, WorldId worldId, string name, FacilityType facility,
        IReadOnlyList<RecipeLine> lines, long laborCost, long ticksToProduce) : base(id)
    {
        WorldId = worldId;
        Name = name;
        Facility = facility;
        Lines = lines;
        LaborCost = laborCost;
        TicksToProduce = ticksToProduce;
    }

    public static ErrorOr<Recipe> Create(WorldId worldId, string name, FacilityType facility,
        IReadOnlyList<RecipeLine> lines, long laborCost, long ticksToProduce)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("recipe.name.blank", "Recipe name must not be blank.");
        if (ticksToProduce <= 0)
            return Error.Validation("recipe.ticks.nonpositive", "Ticks to produce must be positive.");
        if (laborCost < 0)
            return Error.Validation("recipe.laborcost.negative", "Labor cost must not be negative.");
        if (lines.Count == 0 || lines.Any(l => l.Quantity <= 0))
            return Error.Validation("recipe.line.quantity.nonpositive", "All recipe line quantities must be positive.");
        if (!lines.Any(l => l.Kind == RecipeLineKind.Input))
            return Error.Validation("recipe.lines.noinput", "Recipe must contain at least one input line.");
        if (!lines.Any(l => l.Kind == RecipeLineKind.Output))
            return Error.Validation("recipe.lines.nooutput", "Recipe must contain at least one output line.");

        return new Recipe(RecipeId.New(), worldId, name.Trim(), facility, lines, laborCost, ticksToProduce);
    }
}
