using FluentAssertions;
using WorldEcon.Seeding;

namespace WorldEcon.Seeding.Tests.Unit;

public class KankaSeedSourceTests
{
    [Test]
    public async Task LoadAsync_Throws_NotSupported()
    {
        var source = new KankaSeedSource(campaignId: 1, apiToken: "token");

        var act = async () => await source.LoadAsync();
        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*campaign attribute audit*");
    }
}
