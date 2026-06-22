using FluentAssertions;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Domain.Tests.Unit.Geography;

public class GeographyV2Tests
{
    [Test]
    public void Region_BackCompatCreate_IsLandWithCountry()
    {
        var country = CountryId.New();
        var r = Region.Create(WorldId.New(), country, "The Reach").Value;
        r.CountryId.Should().Be(country);
        r.Kind.Should().Be(RegionKind.Land);
    }

    [Test]
    public void Region_OceanCreate_HasNoCountry()
    {
        var r = Region.Create(WorldId.New(), "Ocean of Lost Souls", RegionKind.Ocean).Value;
        r.CountryId.Should().BeNull();
        r.Kind.Should().Be(RegionKind.Ocean);
    }

    [Test]
    public void Region_SetKindAndCountry_Mutate()
    {
        var r = Region.Create(WorldId.New(), "X", RegionKind.Land).Value;
        r.SetKind(RegionKind.Mountain);
        var c = CountryId.New();
        r.SetPrimaryCountry(c);
        r.Kind.Should().Be(RegionKind.Mountain);
        r.CountryId.Should().Be(c);
        r.SetPrimaryCountry(null);
        r.CountryId.Should().BeNull();
    }

    [Test]
    public void RegionContainment_RejectsSelf()
    {
        var rid = RegionId.New();
        RegionContainment.Create(WorldId.New(), rid, rid).IsError.Should().BeTrue();
    }

    [Test]
    public void RegionContainment_Create_LinksParentChild()
    {
        var rc = RegionContainment.Create(WorldId.New(), RegionId.New(), RegionId.New()).Value;
        rc.ParentRegionId.Should().NotBe(rc.ChildRegionId);
    }

    [Test]
    public void Settlement_SetState_Ruined()
    {
        var s = Settlement.Create(WorldId.New(), RegionId.New(), "Zeigelith", SettlementType.City, 0, 0, 0).Value;
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

        var disputes = TerritorialClaim.CreateForRegion(w, CountryId.New(), RegionId.New(), ClaimType.Disputes).Value;
        disputes.TargetKind.Should().Be(ClaimTargetKind.Region);
        disputes.ClaimType.Should().Be(ClaimType.Disputes);
    }

    [Test]
    public void TerritorialClaim_RejectsEmptyTarget()
        => TerritorialClaim.Create(WorldId.New(), CountryId.New(), ClaimType.Controls, ClaimTargetKind.Settlement, Guid.Empty)
            .IsError.Should().BeTrue();
}
