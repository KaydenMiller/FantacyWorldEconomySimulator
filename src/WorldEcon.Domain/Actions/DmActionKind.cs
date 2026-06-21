namespace WorldEcon.Domain.Actions;

/// <summary>Kinds of DM/party effect the action-applying service implements. Explicit values for stable persistence.</summary>
public enum DmActionKind { BuyFromShops = 0, AdjustMarketStock = 1, SetProductionNodeDisabled = 2 }
