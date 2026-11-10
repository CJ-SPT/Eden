using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Targeting;
using Eden.Protocol.Flight;
using Eden.Protocol.Tests.Combat;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerDefenseLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockingAloneDoesNotProvokeButTackleMakesAMiningFitFightWhileTrapped(bool core)
    {
        const long enemy = 90000002;
        const long enemyShip = 1000000000002;
        using var f = CapsuleerDefenseModuleTests.Prepare(3242, DogmaEffect.WarpDisruptor);
        f.Fit((int)CombatWeaponType.CivilianGatlingRailgun);
        f.Fit((int)MiningLaserType.MinerI);
        if (core)
        {
            f.Fit(10998);
        }
        CapsuleerDefenseModuleTests.Space(f, DogmaEffect.WarpDisruptor);
        long point;
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            world.Set<SavedPilotHostility>().ExecuteDelete();
            world.Pilots.Find(enemy)!.SafetyLevel = ShipSafetyLevel.Disabled;
            CapsuleerCommunityStore.GrantRequirements(world, catalog, enemy, 3242, []);
            point = InventoryPersistence.NextId(world);
            world.Add(
                new OwnedItem
                {
                    Id = point,
                    OwnerId = enemy,
                    LocationId = enemyShip,
                    TypeId = 3242,
                    Singleton = true,
                    Online = true,
                    Quantity = 1,
                    FlagId = (int)InventoryFlag.MediumSlot0,
                }
            );
            world.SaveChanges();
        }
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        NavigationState navigation = new();
        var locks = f.Db.Services.GetRequiredService<IPilotTargetLocks>();
        locks.Add(enemyShip, f.ShipId);
        locks.Add(f.ShipId, enemyShip);
        CapsuleerDefenseState previous = new() { ShipId = f.ShipId, Locks = [new(enemyShip, f.Clock.Now)] };
        Assert.Null(controller.Advance(pilot, navigation, previous, f.Clock.Now, 0, []).State);
        Assert.True(locks.Contains(enemyShip, f.ShipId));
        Assert.False(locks.Contains(f.ShipId, enemyShip));
        var tackle = f.Db.Services.GetRequiredService<IPilotTackle>();
        tackle.Activate(
            enemy,
            point,
            DogmaEffect.WarpDisruptor,
            new(f.ShipId, TackleTargetKind.Ship),
            new HashSet<long> { f.ShipId },
            f.Clock.Now
        );
        var scan = controller.Advance(pilot, navigation, null, f.Clock.Now, 0, []);
        Assert.True(scan.NewThreat);
        Assert.True(scan.Escape);
        Assert.False(scan.HoldsNavigation);
        if (core)
        {
            Assert.Contains(scan.State!.Cycles, cycle => cycle.Effect == DogmaEffect.WarpCoreStabilizer);
        }
        f.Clock.Seconds += 30;
        tackle.Activate(
            enemy,
            point,
            DogmaEffect.WarpDisruptor,
            new(f.ShipId, TackleTargetKind.Ship),
            new HashSet<long> { f.ShipId },
            f.Clock.Now
        );
        var defense = controller.Advance(scan.Pilot, navigation, scan.State, f.Clock.Now, 30, []);
        Assert.Contains(defense.State!.Cycles, cycle => cycle.Effect == DogmaEffect.LaserFired);
        tackle.Stop(enemy, enemyShip, point);
        var free = controller.Advance(defense.Pilot, navigation, defense.State, f.Clock.Now, 30, []);
        Assert.True(free.Escape);
        Assert.DoesNotContain(free.State!.Cycles, cycle => cycle.Effect == DogmaEffect.LaserFired);
    }

    [Fact]
    public async Task EmptyMagazineReloadsOwnedCargoThenRetreatsWithoutCreatingCharges()
    {
        using var f = CapsuleerDefenseModuleTests.Prepare(561, DogmaEffect.ProjectileFired);
        long module = f.Fit(561);
        long first = f.Grant(215, 1);
        f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, f.ShipId, [module], [first], f.StationId, f.Clock.Now);
        f.Grant(215, 2, cargo: true);
        f.Clock.Seconds += 60;
        CapsuleerDefenseModuleTests.Space(f, DogmaEffect.ProjectileFired);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var controller = f.Db.Services.GetRequiredService<CapsuleerDefense>();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        NavigationState navigation = new();
        CapsuleerDefenseState? state = null;
        bool escaped = false;
        int volleys = 0;
        for (int turn = 0; turn < 12; turn++)
        {
            var next = controller.Advance(pilot, navigation, state, f.Clock.Now, turn * 30, []);
            pilot = next.Pilot;
            state = next.State;
            if (state!.Cycles.Any(cycle => cycle.ModuleId == module && cycle.Effect == DogmaEffect.ProjectileFired))
            {
                volleys++;
            }
            if (next.Escape)
            {
                escaped = true;
                Assert.DoesNotContain(state.Cycles, cycle => cycle.Effect == DogmaEffect.ProjectileFired);
                break;
            }
            f.Clock.Seconds += 30;
        }
        Assert.True(escaped);
        Assert.Equal(3, volleys);
        await live.FlushAsync();
        using WorldDbContext saved = new(f.Db.Path);
        Assert.DoesNotContain(saved.Set<OwnedItem>(), item => item.OwnerId == f.PilotId && item.TypeId == 215);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DroneRecallReturnsIndividualsOrYieldsToEscapeAfterTenSeconds(bool distant)
    {
        using CombatDroneTests.Fixture f = new();
        var ids = f.Launch(quantity: 2);
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Add(
                new SavedPilotHostility
                {
                    SourceShipId = 1000000000002,
                    TargetShipId = f.Ship,
                    LastAttackAt = f.Now,
                    ExpiresAt = f.Now + TimeSpan.FromMinutes(15).Ticks,
                }
            );
            world.SaveChanges();
        }
        var pilot = f.Base.Db.Store.FindPilot(1000001)!;
        f.Base.Db.Store.SetPosition(1000000000002, pilot.Position! + new SpacePosition(500, 0, 0), pilot.SystemId);
        if (distant)
        {
            f.Base.Db.Store.SetPosition(f.Ship, pilot.Position! + new SpacePosition(100_000, 0, 0), pilot.SystemId);
            pilot = f.Base.Db.Store.FindPilot(1000001)!;
        }
        var enemy = f.Base.Db.Store.FindPilot(1000002)!;
        f.Base.Db.Services.GetRequiredService<SharedSpace>().Publish(new(PilotContact.From(enemy), SpacePosition.Zero, null, 0, null));
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        var controller = f.Base.Db.Services.GetRequiredService<CapsuleerDefense>();
        NavigationState navigation = new();
        var recall = controller.Advance(pilot, navigation, new() { ShipId = f.Ship }, f.Now, 1, [], forceRetreat: true);
        Assert.True(recall.Escape);
        Assert.True(recall.HoldsNavigation);
        if (distant)
        {
            Assert.Equal(f.Now + TimeSpan.FromSeconds(10).Ticks, recall.State!.RecallUntil);
            f.Base.Clock.Seconds += 10;
            var timeout = controller.Advance(recall.Pilot, navigation, recall.State, f.Now, 11, [], forceRetreat: true);
            Assert.True(timeout.Escape);
            Assert.False(timeout.HoldsNavigation);
        }
        else
        {
            f.Base.Clock.Seconds += 2;
            var returned = controller.Advance(recall.Pilot, navigation, recall.State, f.Now, 3, [], forceRetreat: true);
            Assert.False(returned.HoldsNavigation);
            Assert.False(f.Drones.HasControlled(f.Pilot, f.Ship));
            using WorldDbContext saved = new(f.Base.Db.Path);
            Assert.All(ids, id => Assert.Equal((int)InventoryFlag.DroneBay, saved.Set<OwnedItem>().Find(id)!.FlagId));
        }
    }
}
