using FluentAssertions;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Domain.Tests.Unit;

public class MoneyChannelsTests
{
    [Test]
    public void KindOf_ClassifiesEveryChannel()
    {
        MoneyChannels.KindOf(MoneyChannel.ConsumerAllowance).Should().Be(MoneyFlowKind.Faucet);
        MoneyChannels.KindOf(MoneyChannel.MerchantPurchase).Should().Be(MoneyFlowKind.Sink);
        MoneyChannels.KindOf(MoneyChannel.MerchantSale).Should().Be(MoneyFlowKind.Faucet);
    }

    [Test]
    public void KindOf_HandlesAllDefinedChannels()
    {
        // Guards against adding a channel without classifying it.
        foreach (MoneyChannel channel in System.Enum.GetValues<MoneyChannel>())
        {
            var act = () => MoneyChannels.KindOf(channel);
            act.Should().NotThrow($"channel {channel} must be classified");
        }
    }
}
