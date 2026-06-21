using ErrorOr;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public sealed class Good : AggregateRoot<GoodId>
{
    public WorldId WorldId { get; }
    public string Name { get; private set; }
    public GoodCategory Category { get; private set; }
    public Money BaseValue { get; private set; }
    public string BaseUnit { get; private set; }
    public SizeClass Size { get; private set; }
    public long ShelfLifeTicks { get; private set; } // 0 = imperishable
    public bool Divisible { get; private set; }
    public Provenance Provenance { get; private set; }

    private Good() : base(default) { Name = null!; BaseUnit = null!; } // EF

    private Good(GoodId id, WorldId worldId, string name, GoodCategory category, Money baseValue,
        string baseUnit, SizeClass size, long shelfLifeTicks, bool divisible, Provenance provenance) : base(id)
    {
        WorldId = worldId;
        Name = name;
        Category = category;
        BaseValue = baseValue;
        BaseUnit = baseUnit;
        Size = size;
        ShelfLifeTicks = shelfLifeTicks;
        Divisible = divisible;
        Provenance = provenance;
    }

    public static ErrorOr<Good> Create(WorldId worldId, string name, GoodCategory category, Money baseValue,
        string baseUnit, SizeClass size, long shelfLifeTicks, bool divisible)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("good.name.blank", "Good name must not be blank.");
        if (string.IsNullOrWhiteSpace(baseUnit))
            return Error.Validation("good.baseunit.blank", "Base unit must not be blank.");
        if (baseValue.IsNegative)
            return Error.Validation("good.basevalue.negative", "Base value must not be negative.");
        if (shelfLifeTicks < 0)
            return Error.Validation("good.shelflife.negative", "Shelf life must not be negative.");

        return new Good(GoodId.New(), worldId, name.Trim(), category, baseValue,
            baseUnit.Trim(), size, shelfLifeTicks, divisible, Provenance.Authored);
    }
}
