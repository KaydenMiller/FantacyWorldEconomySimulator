using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>An in-progress production batch occupying a <see cref="ProductionNode"/>.</summary>
public sealed class WorkOrder : AggregateRoot<WorkOrderId>
{
    public WorldId WorldId { get; }
    public ProductionNodeId ProductionNodeId { get; private set; }
    public RecipeId RecipeId { get; private set; }
    public Tick StartTick { get; private set; }
    public Tick CompleteTick { get; private set; }
    public Money CommittedInputCost { get; private set; } // total cost of reserved inputs
    public bool Completed { get; private set; }

    private WorkOrder() : base(default) { } // EF

    private WorkOrder(WorkOrderId id, WorldId worldId, ProductionNodeId productionNodeId, RecipeId recipeId,
        Tick startTick, Tick completeTick, Money committedInputCost) : base(id)
    {
        WorldId = worldId;
        ProductionNodeId = productionNodeId;
        RecipeId = recipeId;
        StartTick = startTick;
        CompleteTick = completeTick;
        CommittedInputCost = committedInputCost;
        Completed = false;
    }

    public static ErrorOr<WorkOrder> Create(WorldId worldId, ProductionNodeId productionNodeId, RecipeId recipeId,
        Tick start, Tick complete, Money committedInputCost)
    {
        if (complete.Value <= start.Value)
            return Error.Validation("workorder.complete.notafterstart", "Complete tick must be after start tick.");
        if (committedInputCost.IsNegative)
            return Error.Validation("workorder.committedcost.negative", "Committed input cost must not be negative.");

        return new WorkOrder(WorkOrderId.New(), worldId, productionNodeId, recipeId, start, complete, committedInputCost);
    }

    public void MarkComplete()
    {
        if (Completed)
            throw new InvalidOperationException("Work order is already complete.");
        Completed = true;
    }
}
