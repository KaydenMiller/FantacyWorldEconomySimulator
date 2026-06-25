namespace WorldEcon.Domain.Economy;

/// <summary>How a money flow affects the total money supply.</summary>
public enum MoneyFlowKind
{
    /// <summary>Creates money (e.g. consumer allowance income). Increases total supply.</summary>
    Faucet = 0,
    /// <summary>Destroys money (e.g. a future tax/upkeep). Decreases total supply.</summary>
    Sink = 1,
    /// <summary>Moves money between two in-world holders (e.g. a retail sale). Supply unchanged.</summary>
    Transfer = 2,
}

/// <summary>A named channel through which currency flows. Each is classified as a faucet, sink, or
/// transfer by <see cref="MoneyChannels.KindOf"/>. New channels are added as taxes/tariffs/upkeep/
/// link-fees and the wage loop arrive.</summary>
public enum MoneyChannel
{
    /// <summary>Weekly allowance income granted to consumers (the placeholder faucet until wages exist).</summary>
    ConsumerAllowance = 0,
    /// <summary>A consumer buying from a shop: budget → till. Conserved.</summary>
    RetailSale = 1,
    /// <summary>A merchant buying goods at the source. Today the source shop is not credited → a sink.</summary>
    MerchantPurchase = 2,
    /// <summary>A merchant selling delivered goods. Today no one is debited → a faucet.</summary>
    MerchantSale = 3,
}

/// <summary>Classifies each <see cref="MoneyChannel"/> as a faucet, sink, or transfer.</summary>
public static class MoneyChannels
{
    public static MoneyFlowKind KindOf(MoneyChannel channel) => channel switch
    {
        MoneyChannel.ConsumerAllowance => MoneyFlowKind.Faucet,
        MoneyChannel.RetailSale => MoneyFlowKind.Transfer,
        MoneyChannel.MerchantPurchase => MoneyFlowKind.Sink,
        MoneyChannel.MerchantSale => MoneyFlowKind.Faucet,
        _ => throw new System.ArgumentOutOfRangeException(nameof(channel), channel, "Unclassified money channel."),
    };
}
