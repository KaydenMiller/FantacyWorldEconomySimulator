using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Domain.Economy;

/// <summary>Shared trading-agent contract (spec §3.2, §7.1). Representative merchants implement it now;
/// heroic traders will share it later.</summary>
public interface IMerchantAgent
{
    MerchantId Id { get; }
    SettlementId Seat { get; }
    Money Capital { get; }
    Mass WeightCapacity { get; }    // max mass hauled per caravan
    Volume VolumeCapacity { get; }  // max volume hauled per caravan
    long Reach { get; }             // max graph distance it surveys for opportunities
}
