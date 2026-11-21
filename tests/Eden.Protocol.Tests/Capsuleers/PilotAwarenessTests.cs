using Eden.Data.World.Combat;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Administration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class PilotAwarenessTests
{
    [Fact]
    public void MinerObservesCombatShipLeavesItsBeltAndSheltersAtHome()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        const int system = 30000141;
        using (CatalogDbContext edit = new(f.Database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data=json_set(Data,'$.security',0.2) "
                    + "WHERE Dataset='client/solarsystemcontent' AND Key='30000141'"
            );
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        MinerObservation miner = CapsuleerTestDriver.Miner(host, f.Clock);
        for (int i = 0; i < 1200 && miner.Work.Activity != MinerActivity.Mining; i++)
        {
            miner = CapsuleerTestDriver.Miner(host, f.Clock);
        }
        Assert.Equal(MinerActivity.Mining, miner.Work.Activity);
        long belt = miner.Work.WorkingBelt;
        Assert.True(belt > 0);
        var other = PilotContact.From(miner.Pilot) with
        {
            PilotId = 999000001,
            ShipId = 999000002,
            ShipGroupId = (int)HullClass.StrategicCruiser,
        };
        host.GetRequiredService<SharedSpace>().Publish(new(other, SpacePosition.Zero, null, 0, null));
        for (int i = 0; i < 1200 && miner.Work.WaitingReason != "Sheltering until the belt is clear."; i++)
        {
            miner = CapsuleerTestDriver.Miner(host, f.Clock);
        }
        Assert.Equal(system, miner.Pilot.SystemId);
        Assert.Null(miner.Pilot.Position);
        Assert.Contains(miner.Work.AvoidedBelts, entry => entry.BeltId == belt);
        Assert.True(miner.Work.DueTicks > miner.Work.ActiveTicks);
    }

    [Fact]
    public void CiviliansReadVisibleHullAffiliationAndClosingCourse()
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var pilot = CapsuleerTestDriver.Miner(host, f.Clock).Pilot with { Position = SpacePosition.Zero };
        pilot = pilot with { Identity = pilot.Identity with { AllianceId = 42 } };
        PilotSighting contact = new(100, 101, 603, HullClass.StrategicCruiser, 1, null, new(28_000, 0, 0), SpacePosition.Zero, 30);
        CivilianDanger Assess(PilotSighting other) => PilotAwareness.Assess(pilot, SpacePosition.Zero, [other], 17);
        Assert.Equal(CivilianDanger.Caution, Assess(contact));
        Assert.Equal(CivilianDanger.Caution, Assess(contact with { Hull = HullClass.Marauder }));
        Assert.Equal(CivilianDanger.Clear, Assess(contact with { AllianceId = 42 }));
        Assert.Equal(CivilianDanger.Clear, Assess(contact with { RegionalGroupId = 17 }));
        Assert.Equal(CivilianDanger.Clear, Assess(contact with { Hull = HullClass.Frigate, TypeId = (int)TechOneHullType.Venture }));
        Assert.Equal(CivilianDanger.Caution, Assess(contact with { Velocity = new(1000, 0, 0) }));
        Assert.Equal(CivilianDanger.Escape, Assess(contact with { Velocity = new(-1000, 0, 0) }));
        Assert.Equal(CivilianDanger.Caution, Assess(contact with { Velocity = new(-1000, 5000, 0) }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MinerChangesBeltsWithCargoAndRemembersTheStrippedCircuitAfterRestart(bool strippedOnArrival)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
                    SELECT Id+1,SolarSystemId,TypeId,GroupId,X+1000000000,Y,Z FROM Celestial WHERE Id=40009051;
                INSERT INTO SourceRecord VALUES('client/asteroidcountbytypeidbeltid','40009052','{{"1230":2}}');
                """
            );
        }
        static MinerObservation Until(ServiceProvider host, TestClock clock, Func<MinerObservation, bool> done)
        {
            for (int i = 0; i < 1600; i++)
            {
                var miner = CapsuleerTestDriver.Miner(host, clock);
                if (done(miner))
                {
                    return miner;
                }
            }
            throw new InvalidOperationException("Miner did not reach its expected activity.");
        }

        void Strip(MinerObservation miner, bool leaveLastUnit)
        {
            using WorldActionScope scope = new(f.Database, new(systems: [miner.Pilot.SystemId]));
            foreach (var rock in scope.World.Set<WorldAsteroid>().Where(r => r.BeltId == miner.Work.WorkingBelt))
            {
                rock.Quantity = leaveLastUnit && rock.Id == miner.Work.TargetId ? 1 : 0;
            }
            scope.World.SaveChanges();
            scope.Commit();
        }

        long firstBelt;
        int trips;
        long cargo;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var first = Until(
                host,
                f.Clock,
                miner => miner.Work.Activity == (strippedOnArrival ? MinerActivity.FlyingToBelt : MinerActivity.Mining)
            );
            firstBelt = first.Work.WorkingBelt;
            trips = first.Work.Trips;
            Strip(first, !strippedOnArrival);
            var moving = Until(
                host,
                f.Clock,
                miner => miner.Work.Activity == MinerActivity.FlyingToBelt && miner.Work.WorkingBelt != firstBelt
            );
            Assert.Equal(firstBelt, Assert.Single(moving.Work.StrippedBelts).BeltId);
            Assert.Equal(trips, moving.Work.Trips);
            Assert.Equal(0, moving.Work.PaidUntil);
            cargo = host.GetRequiredService<IPilotInventory>()
                .List(first.Pilot.PilotId, first.Pilot.ShipId)
                .Where(i => i.TypeId == 1230)
                .Sum(i => i.Quantity);
            Assert.Equal(!strippedOnArrival, cargo > 0);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        using var resumed = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var second = Until(resumed, f.Clock, miner => miner.Work.Activity == MinerActivity.Mining);
        Assert.NotEqual(firstBelt, second.Work.WorkingBelt);
        Assert.Equal(firstBelt, Assert.Single(second.Work.StrippedBelts).BeltId);
        Assert.Equal(trips, second.Work.Trips);
        Strip(second, false);
        var waiting = Until(resumed, f.Clock, miner => miner.Work.Activity == MinerActivity.Waiting);
        Assert.Null(waiting.Pilot.Position);
        Assert.Equal(2, waiting.Work.StrippedBelts.Length);
        Assert.True(waiting.Work.DueTicks > waiting.Work.ActiveTicks);
        Assert.Equal("Waiting to revisit stripped belts.", waiting.Work.WaitingReason);
        Assert.Null(resumed.GetRequiredService<LiveWorld>().Economy.Peek<ModuleCooldown>(second.Work.ModuleId));
        var inventory = resumed.GetRequiredService<IPilotInventory>();
        Assert.Equal(cargo, inventory.List(second.Pilot.PilotId, second.Pilot.StationId).Where(i => i.TypeId == 1230).Sum(i => i.Quantity));
    }

    [Fact]
    public void NearbyShipsRespectVisibilityMovementAndBoundedCrowds()
    {
        SharedSpace space = new();
        const int system = 100;
        var origin = new SpacePosition(249_999, 0, 0);
        ShipContact At(long id, double x) =>
            new(
                new(
                    id,
                    "Test",
                    1,
                    0,
                    "",
                    id,
                    603,
                    (int)HullClass.Frigate,
                    6,
                    "Ship",
                    new FlightProperties(1, 40, 1000000, 1, 250, 5),
                    system,
                    new(x, 0, 0),
                    0,
                    0,
                    0
                ),
                SpacePosition.Zero,
                null,
                0,
                null
            );
        space.Publish(At(1, 250_001));
        space.Publish(At(2, -1));
        space.Publish(At(3, -2));
        space.Publish(At(4, 250_000) with { Cloaked = true });
        space.Publish(At(5, 250_000) with { Arriving = true });
        Assert.Equal(new long[] { 1, 2 }, space.Observe(system, 99, origin, 0).Select(s => s.ShipId));
        space.Publish(At(1, 2_000_000));
        space.Leave(2);
        Assert.Empty(space.Observe(system, 99, origin, 0));
        for (int i = 100; i < 2100; i++)
        {
            space.Publish(At(i, 250_001));
        }
        using var metrics = SessionPerformance.Begin("crowded capsuleer sightings", _ => { }, force: true)!;
        var first = space.Observe(system, 99, origin, 0);
        Assert.Equal(12, first.Length);
        Assert.NotEqual(first, space.Observe(system, 99, origin, 15));
        Assert.Empty(space.Observe(system + 1, 99, origin, 0));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void ClearedBeltsReturnAfterExpiryAndFightsUsePaidWeaponReach()
    {
        var memory = PilotAwareness.Remember([], 15, 10);
        Assert.Equal(16L, Assert.Single(PilotAwareness.Circuit([15, 16], 1, 30, memory, 11)));
        Assert.Equal(2, PilotAwareness.Circuit([15, 16], 1, 30, memory, 10 + PilotAwareness.MemoryTicks).Count());
        var gun = new CombatWeapon(1, 1, 1, default!, 1, 3000, 5000, 1, 1, 1);
        Assert.Equal(5000, PilotAwareness.FightingRange([new(gun, 8)], 25000, 500));
        var missile = new RattingWeapon(gun with { Optimal = 50000 }, 8, new(default!, 1, 1, 1));
        Assert.Equal(25000, PilotAwareness.FightingRange([missile, new(gun, 0)], 25000, 500));
        var form = SettingsForm.From(new());
        form.SituationalAwareness = form.BeltRatting = form.AnomalyRatting = false;
        var policy = form.Build().Capsuleers;
        Assert.False(policy.SituationalAwareness || policy.BeltRatting || policy.AnomalyRatting);
    }
}
