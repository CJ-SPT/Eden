using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Administration;
using Eden.World.Catalog;
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
        var other = PilotContact.From(miner.Pilot) with { PilotId = 999000001, ShipId = 999000002, ShipGroupId = (int)HullClass.Cruiser };
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
