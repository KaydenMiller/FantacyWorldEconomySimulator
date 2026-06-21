using FluentAssertions;
using WorldEcon.Seeding;

namespace WorldEcon.Seeding.Tests.Unit;

public class JsonSeedSourceTests
{
    private const string SampleJson = """
    {
      "name": "Testoria",
      "seed": 99,
      "rulesetVersion": "1.0.0",
      "goods": [
        { "name": "Bread", "category": "Food", "baseValue": 30, "baseUnit": "loaf",
          "size": "Small", "shelfLifeTicks": 4320, "divisible": true, "consumptionPerCapitaBp": 50 }
      ],
      "recipes": [],
      "continents": [
        { "name": "Mundus", "countries": [
          { "name": "Highmark", "regions": [
            { "name": "The Reach", "settlements": [
              { "name": "Hammerfell", "type": "City", "x": 10, "y": 20, "population": 50000 }
            ]}
          ]}
        ]}
      ],
      "routes": []
    }
    """;

    [Test]
    public async Task LoadAsync_Parses_TopLevel_Goods_And_Settlement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seed_{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, SampleJson);
        try
        {
            var seed = await new JsonSeedSource(path).LoadAsync();

            seed.Name.Should().Be("Testoria");
            seed.Seed.Should().Be(99UL);
            seed.RulesetVersion.Should().Be("1.0.0");

            seed.Goods.Should().ContainSingle();
            var bread = seed.Goods[0];
            bread.Name.Should().Be("Bread");
            bread.Category.Should().Be("Food");
            bread.BaseValue.Should().Be(30);
            bread.Divisible.Should().BeTrue();

            var settlement = seed.Continents[0].Countries[0].Regions[0].Settlements[0];
            settlement.Name.Should().Be("Hammerfell");
            settlement.Type.Should().Be("City");
            settlement.Population.Should().Be(50000);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task LoadAsync_MissingFile_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.json");
        var source = new JsonSeedSource(path);

        var act = async () => await source.LoadAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }
}
