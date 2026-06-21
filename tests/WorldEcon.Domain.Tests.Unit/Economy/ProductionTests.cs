using FluentAssertions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Domain.Tests.Unit.Economy;

public class ProductionTests
{
    private static IReadOnlyList<RecipeLine> ValidLines()
        => new[]
        {
            new RecipeLine(GoodId.New(), 3, RecipeLineKind.Input),
            new RecipeLine(GoodId.New(), 1, RecipeLineKind.Output),
        };

    // ---------- Recipe ----------

    [Test]
    public void Recipe_Create_SetsFields()
    {
        var world = WorldId.New();
        var lines = ValidLines();
        var r = Recipe.Create(world, "Bread", FacilityType.Bakery, lines, laborCost: 5, ticksToProduce: 60).Value;

        r.WorldId.Should().Be(world);
        r.Name.Should().Be("Bread");
        r.Facility.Should().Be(FacilityType.Bakery);
        r.Lines.Should().BeEquivalentTo(lines);
        r.LaborCost.Should().Be(5);
        r.TicksToProduce.Should().Be(60);
    }

    [Test]
    public void Recipe_Create_TrimsName()
        => Recipe.Create(WorldId.New(), "  Bread  ", FacilityType.Bakery, ValidLines(), 5, 60)
            .Value.Name.Should().Be("Bread");

    [Test]
    public void Recipe_Inputs_And_Outputs_Helpers()
    {
        var r = Recipe.Create(WorldId.New(), "Bread", FacilityType.Bakery, ValidLines(), 0, 1).Value;
        r.Inputs.Should().OnlyContain(l => l.Kind == RecipeLineKind.Input);
        r.Outputs.Should().OnlyContain(l => l.Kind == RecipeLineKind.Output);
        r.Inputs.Should().HaveCount(1);
        r.Outputs.Should().HaveCount(1);
    }

    [Test]
    public void Recipe_Create_RejectsBlankName()
        => Recipe.Create(WorldId.New(), "  ", FacilityType.Bakery, ValidLines(), 5, 60)
            .IsError.Should().BeTrue();

    [Test]
    public void Recipe_Create_RejectsNonPositiveTicks()
        => Recipe.Create(WorldId.New(), "Bread", FacilityType.Bakery, ValidLines(), 5, 0)
            .IsError.Should().BeTrue();

    [Test]
    public void Recipe_Create_RejectsNegativeLaborCost()
        => Recipe.Create(WorldId.New(), "Bread", FacilityType.Bakery, ValidLines(), -1, 60)
            .IsError.Should().BeTrue();

    [Test]
    public void Recipe_Create_RejectsNoInput()
    {
        var lines = new[] { new RecipeLine(GoodId.New(), 1, RecipeLineKind.Output) };
        Recipe.Create(WorldId.New(), "Bread", FacilityType.Bakery, lines, 5, 60).IsError.Should().BeTrue();
    }

    [Test]
    public void Recipe_Create_RejectsNoOutput()
    {
        var lines = new[] { new RecipeLine(GoodId.New(), 1, RecipeLineKind.Input) };
        Recipe.Create(WorldId.New(), "Bread", FacilityType.Bakery, lines, 5, 60).IsError.Should().BeTrue();
    }

    [Test]
    public void Recipe_Create_RejectsNonPositiveLineQuantity()
    {
        var lines = new[]
        {
            new RecipeLine(GoodId.New(), 0, RecipeLineKind.Input),
            new RecipeLine(GoodId.New(), 1, RecipeLineKind.Output),
        };
        Recipe.Create(WorldId.New(), "Bread", FacilityType.Bakery, lines, 5, 60).IsError.Should().BeTrue();
    }

    // ---------- ProductionNode ----------

    [Test]
    public void ProductionNode_Create_SetsFields()
    {
        var world = WorldId.New();
        var settlement = SettlementId.New();
        var recipe = RecipeId.New();
        var n = ProductionNode.Create(world, settlement, recipe, FacilityType.Mine, throughputCap: 3).Value;

        n.WorldId.Should().Be(world);
        n.SettlementId.Should().Be(settlement);
        n.RecipeId.Should().Be(recipe);
        n.Facility.Should().Be(FacilityType.Mine);
        n.ThroughputCap.Should().Be(3);
    }

    [Test]
    public void ProductionNode_Create_RejectsThroughputBelowOne()
        => ProductionNode.Create(WorldId.New(), SettlementId.New(), RecipeId.New(), FacilityType.Mine, 0)
            .IsError.Should().BeTrue();

    [Test]
    public void ProductionNode_Create_IsNotDisabled()
        => ProductionNode.Create(WorldId.New(), SettlementId.New(), RecipeId.New(), FacilityType.Mine, 3)
            .Value.Disabled.Should().BeFalse();

    [Test]
    public void ProductionNode_Disable_ThenEnable_TogglesFlag()
    {
        var n = ProductionNode.Create(WorldId.New(), SettlementId.New(), RecipeId.New(), FacilityType.Mine, 3).Value;
        n.Disable();
        n.Disabled.Should().BeTrue();
        n.Enable();
        n.Disabled.Should().BeFalse();
    }

    // ---------- ResourceEndowment ----------

    [Test]
    public void ResourceEndowment_Create_SetsFields()
    {
        var world = WorldId.New();
        var settlement = SettlementId.New();
        var good = GoodId.New();
        var e = ResourceEndowment.Create(world, settlement, good, abundance: 100).Value;

        e.WorldId.Should().Be(world);
        e.SettlementId.Should().Be(settlement);
        e.GoodId.Should().Be(good);
        e.Abundance.Should().Be(100);
    }

    [Test]
    public void ResourceEndowment_Create_AllowsZeroAbundance()
        => ResourceEndowment.Create(WorldId.New(), SettlementId.New(), GoodId.New(), 0)
            .IsError.Should().BeFalse();

    [Test]
    public void ResourceEndowment_Create_RejectsNegativeAbundance()
        => ResourceEndowment.Create(WorldId.New(), SettlementId.New(), GoodId.New(), -1)
            .IsError.Should().BeTrue();

    // ---------- WorkOrder ----------

    [Test]
    public void WorkOrder_Create_SetsFields_NotCompleted()
    {
        var world = WorldId.New();
        var node = ProductionNodeId.New();
        var recipe = RecipeId.New();
        var w = WorkOrder.Create(world, node, recipe, new Tick(10), new Tick(70), new Money(500)).Value;

        w.WorldId.Should().Be(world);
        w.ProductionNodeId.Should().Be(node);
        w.RecipeId.Should().Be(recipe);
        w.StartTick.Should().Be(new Tick(10));
        w.CompleteTick.Should().Be(new Tick(70));
        w.CommittedInputCost.Should().Be(new Money(500));
        w.Completed.Should().BeFalse();
    }

    [Test]
    public void WorkOrder_Create_RejectsCompleteNotAfterStart()
        => WorkOrder.Create(WorldId.New(), ProductionNodeId.New(), RecipeId.New(), new Tick(70), new Tick(70), Money.Zero)
            .IsError.Should().BeTrue();

    [Test]
    public void WorkOrder_Create_RejectsNegativeCommittedCost()
        => WorkOrder.Create(WorldId.New(), ProductionNodeId.New(), RecipeId.New(), new Tick(10), new Tick(70), new Money(-1))
            .IsError.Should().BeTrue();

    [Test]
    public void WorkOrder_MarkComplete_SetsCompleted()
    {
        var w = WorkOrder.Create(WorldId.New(), ProductionNodeId.New(), RecipeId.New(), new Tick(10), new Tick(70), Money.Zero).Value;
        w.MarkComplete();
        w.Completed.Should().BeTrue();
    }

    [Test]
    public void WorkOrder_MarkComplete_TwiceThrows()
    {
        var w = WorkOrder.Create(WorldId.New(), ProductionNodeId.New(), RecipeId.New(), new Tick(10), new Tick(70), Money.Zero).Value;
        w.MarkComplete();
        var act = () => w.MarkComplete();
        act.Should().Throw<InvalidOperationException>();
    }
}
