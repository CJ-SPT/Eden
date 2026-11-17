using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Server.Administration;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class PvpTacticsTests
{
    [Fact]
    public void KiterKeepsWeaponRangeWhileTacklerClosesAndBrawlerOrbits()
    {
        Assert.Equal(20_000, PvpTactics.Range(20_000, 30_000, 9_000, PvpRole.Kiter, alliedTackle: true));
        Assert.Equal(7_200, PvpTactics.Range(20_000, 30_000, 9_000, PvpRole.Kiter));
        Assert.True(PvpTactics.Band(20_000, 30_000, 9_000, PvpRole.Kiter).Maximum < 9_000);
        Assert.Equal(7_200, PvpTactics.Range(20_000, 30_000, 9_000, PvpRole.Tackle));
        Assert.Equal(PvpRole.Kiter, PvpTactics.Role(20_000, 9_000));
        Assert.Equal(PvpRole.Tackle, PvpTactics.Role(3_000, 9_000));
        Assert.Equal(PvpRole.ElectronicWarfare, PvpTactics.Role(20_000, 9_000, true));
        Assert.False(PvpTactics.Propel(20_000, 20_000, 1, false));
        Assert.True(PvpTactics.Propel(5_000, 20_000, 1, false));
        Assert.False(PvpTactics.Propel(5_000, 20_000, .1, false));
        Assert.True(PvpTactics.Propel(5_000, 20_000, .1, true));
    }

    [Fact]
    public void RangeUsesMainWeaponsAndDroneControlRatherThanAuxiliaryOrDroneGunRange()
    {
        RattingWeapon rail = new(new(1, 561, 215, new(0, 0, 100, 0), 1_000, 20_000, 0, 1, 40, 0), 100);
        var auxiliary = rail with { Weapon = rail.Weapon with { Damage = new(0, 0, 1, 0), Optimal = 500 } };
        var drone = auxiliary with
        {
            DroneControlRange = 40_000,
            DroneSpeed = 2_000,
            DroneOrbitSpeed = 100,
            Weapon = auxiliary.Weapon with { Optimal = 1_000 },
        };
        Assert.Equal(20_000, PvpTactics.WeaponRange([rail, auxiliary, drone], 50_000, 500));
        Assert.Equal(30_000, PvpTactics.WeaponRange([drone], 50_000, 500));
        Assert.Equal(15_000, PvpTactics.WeaponRange([drone], 15_000, 500));
        Assert.True(PvpTactics.Application(drone, 20_000, 10, 40, 100) > 0);
        Assert.Equal(0, PvpTactics.Application(drone, 50_000, 0, 40, 100));
        Assert.Equal(0, PvpTactics.Application(drone, 20_000, 0, 40, 3_000));
    }

    [Fact]
    public void MovementSpiralsForTackleMatchesVelocityForTrackingAndAnticipatesKitingOvershoot()
    {
        RattingWeapon rail = new(new(1, 561, 215, new(0, 0, 100, 0), 1_000, 20_000, 0, .01, 40, 0), 100);
        var band = new PvpRangeBand(900, 1_000, 1_100);
        var tracking = PvpTactics.Movement(
            [rail],
            new(1_000, 0, 0),
            new(0, 1_000, 0),
            SpacePosition.Zero,
            40,
            1_000,
            band,
            PvpRole.Brawler
        );
        Assert.Equal(PvpMovementKind.MatchVelocity, tracking.Kind);
        Assert.Equal(0, tracking.SpeedFraction);
        var tackle = PvpTactics.Movement(
            [rail],
            new(10_000, 0, 0),
            SpacePosition.Zero,
            SpacePosition.Zero,
            40,
            1_000,
            band,
            PvpRole.Tackle
        );
        Assert.Equal(PvpMovementKind.Spiral, tackle.Kind);
        Assert.InRange(tackle.Direction!.Length, .999, 1.001);
        Assert.True(tackle.Direction.X > 0 && Math.Abs(tackle.Direction.Z) > 0);
        var kiter = PvpTactics.Movement([rail], new(1_000, 0, 0), new(100, 0, 0), new(-100, 0, 0), 40, 1_000, band, PvpRole.Kiter);
        Assert.Equal(PvpMovementKind.Evade, kiter.Kind);
        Assert.True(kiter.Direction!.X < 0);
    }

    [Fact]
    public void PursuitUsesABoundedHorizonAndObservedVelocity()
    {
        Assert.Equal(50_000, PvpTactics.PursuitDistance(new(50_000, 0, 0), SpacePosition.Zero, 0));
        Assert.Equal(45_000, PvpTactics.PursuitDistance(new(50_000, 0, 0), SpacePosition.Zero, 500));
        Assert.Equal(50_000, PvpTactics.PursuitDistance(new(50_000, 0, 0), new(500, 0, 0), 600));
        Assert.Equal(0, PvpTactics.PursuitDistance(new(5_000, 0, 0), SpacePosition.Zero, 500));
        Assert.Equal(50_000, PvpTactics.PursuitDistance(new(50_000, 0, 0), SpacePosition.Zero, double.PositiveInfinity));
    }

    [Fact]
    public void TargetChoiceAccountsForThreatApplicationReportsAndCommitment()
    {
        RattingWeapon[] weapons = [new(new(1, 561, 215, new(0, 0, 10, 0), 1_000, 10_000, 5_000, 1, 40, 0), 100)];
        PilotSighting first = new(1, 10, 603, HullClass.Frigate, 1, null, new(5_000, 0, 0), SpacePosition.Zero, 40);
        var second = first with { PilotId = 2, ShipId = 20 };
        PvpDecision Choose(PilotSighting[] opponents, double strength, long target = 0, long elapsed = 0, long report = 0) =>
            PvpTactics.Select(SpacePosition.Zero, SpacePosition.Zero, weapons, opponents, strength, target, 0, elapsed, report);
        Assert.Equal(10, Choose([first, second], 2).TargetId);
        Assert.Equal(20, Choose([first, second], 2, report: 20).TargetId);
        Assert.Equal(20, Choose([first, second], 2, target: 10, report: 20).TargetId);
        Assert.Equal(20, Choose([first, second], 2, target: 10, elapsed: PvpTactics.CommitmentTicks, report: 20).TargetId);
        Assert.Equal(0, Choose([first with { Hull = HullClass.Battleship }], 1).TargetId);
        Assert.Equal(0, Choose([first with { AllianceId = 1 }, second with { AllianceId = 1 }], 1).TargetId);
        Assert.Equal(0, Choose([first with { RegionalGroupId = 1 }, second with { RegionalGroupId = 1 }], 1).TargetId);
        Assert.Equal(10, Choose([first with { RegionalGroupId = 1 }, second with { RegionalGroupId = 2 }], 1).TargetId);
        Assert.Equal(10, Choose([first, second], 1).TargetId);
        Assert.Equal(0, Choose([first with { Position = new(50_000, 0, 0), Velocity = new(0, 1_000_000, 0) }], 1).TargetId);
        Assert.Equal(
            0,
            PvpTactics
                .Select(
                    SpacePosition.Zero,
                    SpacePosition.Zero,
                    weapons.Select(w => w with { AvailableShots = 0 }).ToArray(),
                    [first],
                    1,
                    0,
                    0,
                    0
                )
                .TargetId
        );
        Assert.True(PvpTactics.Exhausted(1, weapons.Select(w => w with { AvailableShots = 0 }).ToArray()));
        Assert.True(PvpTactics.Exhausted(.01, weapons.Select(w => w with { Weapon = w.Weapon with { CapacitorNeed = 5 } }).ToArray()));
        Assert.False(PvpTactics.Exhausted(.01, weapons));
    }

    [Fact]
    public void GangReplacesItsLeaderWithdrawsAfterLossesAndExpiresReports()
    {
        var gang = new RoamingGang(
            1,
            2,
            1,
            100,
            200,
            [300, 400],
            1,
            [new(1, 10, PvpRole.Scout), new(2, 20, PvpRole.Tackle), new(3, 30, PvpRole.Damage), new(4, 40, PvpRole.Kiter)],
            RoamingGangRules.FormationTicks,
            RoamingGangRules.SortieTicks
        )
        {
            Phase = RoamingGangPhase.Hunting,
        };
        var changed = RoamingGangRules.Reconcile(gang, new HashSet<long> { 20, 30, 40 }, 0);
        Assert.Equal(2, changed.LeaderId);
        Assert.Equal(RoamingGangMemberStatus.Lost, changed.Members[0].Status);
        Assert.Equal(RoamingGangMemberStatus.Active, gang.Members[0].Status);
        Assert.Equal(RoamingGangPhase.Hunting, changed.Phase);
        var retreat = RoamingGangRules.Reconcile(changed, new HashSet<long> { 20, 30 }, 0);
        Assert.Equal(RoamingGangPhase.Returning, retreat.Phase);
        Assert.Same(retreat, RoamingGangRules.Reconcile(retreat, new HashSet<long> { 20, 30 }, 0));
        Assert.Equal(
            RoamingGangPhase.Returning,
            RoamingGangRules.Reconcile(gang, new HashSet<long> { 10, 20, 30, 40 }, gang.ReturnAt).Phase
        );
        var report = RoamingGangRules.Report(gang, 99, 300, 500, 0);
        Assert.Equal(99, RoamingGangRules.Reported(report, 300, 500, 0));
        Assert.Equal(0, RoamingGangRules.Reported(report, 400, 500, 0));
        Assert.Equal(0, RoamingGangRules.Reported(report, 300, 500, RoamingGangRules.ReportTicks));
        Assert.True(RoamingGangRules.Departure(gang, PvpRole.Scout) < RoamingGangRules.Departure(gang, PvpRole.Damage));
    }

    [Fact]
    public void GangSettingsRoundTripAndRejectUnboundedSizes()
    {
        var form = SettingsForm.From(new());
        Assert.True(form.RoamingGangs);
        Assert.Equal(4, form.RoamingGangSize);
        form.RoamingGangs = false;
        form.RoamingGangSize = 6;
        var settings = form.Build();
        Assert.False(SettingsForm.From(settings).RoamingGangs);
        Assert.Equal(6, SettingsForm.From(settings).RoamingGangSize);
        Assert.Throws<InvalidDataException>(() => CapsuleerPopulationRules.Validate(settings.Capsuleers with { RoamingGangSize = 7 }));
    }
}
