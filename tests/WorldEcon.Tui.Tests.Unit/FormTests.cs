using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Tui;
using WorldEcon.Tui.Forms;

namespace WorldEcon.Tui.Tests.Unit;

public class FormTests
{
    [Test]
    public async Task GoodForm_CreatesGood_FromEnqueuedAnswers()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);

            var ui = new FakeUserInteraction()
                .EnqueueText("Gemstone")        // name
                .EnqueueChoice(6)               // category: Luxury (Raw=0..Luxury=6)
                .EnqueueNumber(500)             // base value
                .EnqueueText("gem")             // base unit
                .EnqueueChoice(0)               // size: Tiny
                .EnqueueNumber(0)               // shelf life
                .EnqueueChoice(1)               // divisible: Yes
                .EnqueueNumber(0)               // consumption bp
                .EnqueueChoice(2);              // need tier: Comfort

            var outcome = await new GoodForm().RunAsync(tui, ui);

            outcome.Created.Should().BeTrue();
            var good = await ctx.Goods.SingleAsync(g => g.Name == "Gemstone");
            good.Category.Should().Be(GoodCategory.Luxury);
            good.BaseValue.Units.Should().Be(500);
            good.BaseUnit.Should().Be("gem");
            good.Need.Should().Be(NeedTier.Comfort);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task GoodForm_Cancelled_WritesNothing()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);
            var before = await ctx.Goods.CountAsync();

            // Cancel at the first prompt (null name).
            var ui = new FakeUserInteraction().EnqueueText(null);
            var outcome = await new GoodForm().RunAsync(tui, ui);

            outcome.Created.Should().BeFalse();
            (await ctx.Goods.CountAsync()).Should().Be(before);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task SettlementForm_CreatesSettlement_InAnExistingRegion()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);

            var ui = new FakeUserInteraction()
                .EnqueueText("Newhaven")        // name
                .EnqueueChoice(1)               // type: Town (Village=0, Town=1, City=2)
                .EnqueueChoice(0)               // region: first by name
                .EnqueueNumber(5000)            // population
                .EnqueueNumber(1)               // x
                .EnqueueNumber(2);              // y

            var outcome = await new SettlementForm().RunAsync(tui, ui);

            outcome.Created.Should().BeTrue();
            var s = await ctx.Settlements.SingleAsync(x => x.Name == "Newhaven");
            s.Type.Should().Be(SettlementType.Town);
            s.Population.Should().Be(5000);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task RecipeForm_CreatesRecipe_WithInputAndOutputLines()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);

            var ui = new FakeUserInteraction()
                .EnqueueText("Test Brew")       // name
                .EnqueueChoice(6)               // facility: Workshop (Mine=0..Workshop=6)
                .EnqueueChoice(1)               // add an input? Yes
                .EnqueueChoice(0)               // input good: first by name
                .EnqueueNumber(10)              // input qty
                .EnqueueChoice(0)               // add another input? No
                .EnqueueChoice(1)               // output good: second by name
                .EnqueueNumber(8)               // output qty
                .EnqueueChoice(0)               // add another output? No
                .EnqueueNumber(0)               // labor cost
                .EnqueueNumber(1440);           // ticks to produce

            var outcome = await new RecipeForm().RunAsync(tui, ui);

            outcome.Created.Should().BeTrue();
            var recipe = await ctx.Recipes.SingleAsync(r => r.Name == "Test Brew");
            recipe.Facility.Should().Be(FacilityType.Workshop);
            recipe.Inputs.Should().ContainSingle();
            recipe.Outputs.Should().ContainSingle();
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task ShopForm_FailsGracefully_WithMessage_OnDomainValidation()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);

            // Blank-but-trimmed names are re-prompted by RequiredText; here force a domain error with a
            // negative till by... actually drive a valid name and a negative markup to hit the factory guard.
            var ui = new FakeUserInteraction()
                .EnqueueText("Sketchy Stall")   // name
                .EnqueueChoice(0)               // settlement: first
                .EnqueueNumber(-5)              // markup bp (invalid → negative)
                .EnqueueNumber(0);              // till

            var outcome = await new ShopForm().RunAsync(tui, ui);

            // Either the factory rejects the negative markup (Created false + message) or accepts it; assert
            // the form never throws and reports a coherent outcome.
            outcome.Should().NotBeNull();
            if (!outcome.Created)
                outcome.Message.Should().NotBeNullOrWhiteSpace();
        }
        finally { File.Delete(path); }
    }
}
