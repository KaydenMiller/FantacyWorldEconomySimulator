using FluentAssertions;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class GeographyV2Tests
{
    [Test]
    public void Region_Create_WithCountry()
    {
        var country = CountryId.New();
        var r = PoliticalRegion.Create(WorldId.New(), "The Reach", country).Value;
        r.CountryId.Should().Be(country);
    }

    [Test]
    public void Region_OceanCreate_HasNoCountry()
    {
        var r = PoliticalRegion.Create(WorldId.New(), "Ocean of Lost Souls").Value;
        r.CountryId.Should().BeNull();
    }

    [Test]
    public void Region_SetPrimaryCountry_Mutate()
    {
        var r = PoliticalRegion.Create(WorldId.New(), "X").Value;
        var c = CountryId.New();
        r.SetPrimaryCountry(c);
        r.CountryId.Should().Be(c);
        r.SetPrimaryCountry(null);
        r.CountryId.Should().BeNull();
    }

    [Test]
    public void Settlement_SetState_Ruined()
    {
        var s = Settlement.Create(WorldId.New(), PoliticalRegionId.New(), "Zeigelith", SettlementType.City, 0, 0, 0).Value;
        s.State.Should().Be(SettlementState.Active);
        s.SetState(SettlementState.Ruined);
        s.State.Should().Be(SettlementState.Ruined);
    }

    [Test]
    public void TerritorialClaim_ForSettlement_AndRegion()
    {
        var w = WorldId.New();
        var controls = TerritorialClaim.CreateForSettlement(w, CountryId.New(), SettlementId.New(), ClaimType.Controls).Value;
        controls.TargetKind.Should().Be(ClaimTargetKind.Settlement);
        controls.ClaimType.Should().Be(ClaimType.Controls);

        var disputes = TerritorialClaim.CreateForRegion(w, CountryId.New(), PoliticalRegionId.New(), ClaimType.Disputes).Value;
        disputes.TargetKind.Should().Be(ClaimTargetKind.PoliticalRegion);
        disputes.ClaimType.Should().Be(ClaimType.Disputes);
    }

    [Test]
    public void TerritorialClaim_RejectsEmptyTarget()
        => TerritorialClaim.Create(WorldId.New(), CountryId.New(), ClaimType.Controls, ClaimTargetKind.Settlement, Guid.Empty)
            .IsError.Should().BeTrue();
}
