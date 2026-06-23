using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

/// <summary>A settlement's representative consumer (Phase 2): represents <see cref="Size"/> people,
/// holds a <see cref="Budget"/>, and buys to meet needs. Mirror of <see cref="RepresentativeMerchant"/>.</summary>
public sealed class RepresentativeConsumer : AggregateRoot<ConsumerId>
{
    public WorldId WorldId { get; }
    public SettlementId Seat { get; private set; }
    public long Size { get; private set; }     // number of people represented
    public Money Budget { get; private set; }

    private RepresentativeConsumer() : base(default) { } // EF

    private RepresentativeConsumer(ConsumerId id, WorldId worldId, SettlementId seat, long size, Money budget) : base(id)
    {
        WorldId = worldId;
        Seat = seat;
        Size = size;
        Budget = budget;
    }

    public static ErrorOr<RepresentativeConsumer> Create(WorldId worldId, SettlementId seat, long size, Money budget)
    {
        if (size < 1)
            return Error.Validation("consumer.size.tooSmall", "Size must be at least 1.");
        if (budget.IsNegative)
            return Error.Validation("consumer.budget.negative", "Budget must not be negative.");
        return new RepresentativeConsumer(ConsumerId.New(), worldId, seat, size, budget);
    }

    /// <summary>Spend from the budget (e.g. a purchase). Cannot go into debt.</summary>
    public void Spend(Money amount)
    {
        if (amount.IsNegative)
            throw new InvalidOperationException("Cannot spend a negative amount.");
        if (amount.Units > Budget.Units)
            throw new InvalidOperationException("Cannot spend more than available budget.");
        Budget -= amount;
    }

    /// <summary>Add income / refund to the budget.</summary>
    public void Earn(Money amount)
    {
        if (amount.IsNegative)
            throw new InvalidOperationException("Cannot earn a negative amount.");
        Budget += amount;
    }
}
