using FluentAssertions;
using WorldEcon.Tui.Resources;

namespace WorldEcon.Tui.Tests.Unit;

public class ResourceTests
{
    [Test]
    public async Task Resources_Load_ReturnExpectedColumnsAndRows()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);

            var cities = await new CitiesResource().LoadAsync(tui);
            cities.Columns.Should().Equal("Name", "Type", "Population", "Region");
            cities.Rows.Select(r => r.Cells[0]).Should().Contain(["Hammerfell", "Riverwood"]);

            var goods = await new GoodsResource().LoadAsync(tui);
            goods.Columns.Should().Equal("Name", "Category", "BaseValue", "ShelfLife", "ConsumptionBp");
            goods.Rows.Select(r => r.Cells[0]).Should().Contain("Health Potion");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task CitiesResource_Details_IncludeKeyFields()
    {
        var path = await TestWorld.SeedTempDbAsync();
        try
        {
            await using var ctx = TestWorld.NewContext(path);
            var tui = await TuiContext.LoadAsync(ctx);

            var cities = await new CitiesResource().LoadAsync(tui);
            var hammerfell = cities.Rows.Single(r => r.Cells[0] == "Hammerfell");

            var details = await new CitiesResource().DetailsAsync(hammerfell.Key, tui);

            details.Should().Contain("Name: Hammerfell");
            details.Should().Contain("Type: City");
            details.Should().Contain("Population: 50000");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
