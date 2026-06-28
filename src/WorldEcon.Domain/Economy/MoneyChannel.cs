namespace WorldEcon.Domain.Economy;

/// <summary>How a money flow affects the total money supply. The supply ledger tracks only flows that
/// change the supply (faucets and sinks); conserved place-to-place movement is the concern of the
/// separate (future) geographic money-flow feature, not this ledger.</summary>
public enum MoneyFlowKind
{
    /// <summary>Creates money (e.g. consumer allowance income). Increases total supply.</summary>
    Faucet = 0,
    /// <summary>Destroys money (e.g. a future tax/upkeep). Decreases total supply.</summary>
    Sink = 1,
}

/// <summary>A named channel through which currency is created or destroyed. Each is classified as a
/// faucet or sink by <see cref="MoneyChannels.KindOf"/>. New channels are added as taxes/tariffs/
/// upkeep/link-fees, party gold injection, and the wage loop arrive.</summary>
public enum MoneyChannel
{
    /// <summary>Weekly allowance income granted to consumers (the placeholder faucet until wages exist).</summary>
    ConsumerAllowance = 0,
    /// <summary>A merchant buying goods at the source. Today the source shop is not credited → a sink.</summary>
    MerchantPurchase = 2,
    /// <summary>A merchant selling delivered goods. Today no one is debited → a faucet.</summary>
    MerchantSale = 3,
    /// <summary>What a merchant pays to move a caravan (porter/teamster wages until the labour loop
    /// exists; also the future hook for paid guards/mercenaries). A sink.</summary>
    MerchantHaulage = 4,
}

/// <summary>Classifies each <see cref="MoneyChannel"/> as a faucet or sink.</summary>
public static class MoneyChannels
{
    public static MoneyFlowKind KindOf(MoneyChannel channel) => channel switch
    {
        MoneyChannel.ConsumerAllowance => MoneyFlowKind.Faucet,
        MoneyChannel.MerchantPurchase => MoneyFlowKind.Sink,
        MoneyChannel.MerchantSale => MoneyFlowKind.Faucet,
        MoneyChannel.MerchantHaulage => MoneyFlowKind.Sink,
        _ => throw new System.ArgumentOutOfRangeException(nameof(channel), channel, "Unclassified money channel."),
    };
}
