using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Ammunition;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class LiveCombatObjectTests
{
    private static void Ready(AmmunitionTests.Fixture fixture)
    {
        fixture.Load(quantity: 400);
        fixture.Clock.Milliseconds += 5000;
        fixture.Space();
    }

    [Fact]
    public async Task WarmShotsAndHealthReadsUseObjectsUntilAnExplicitProjectionAndReopen()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        Ready(fixture);
        _ = fixture.Fire();
        fixture.Clock.Milliseconds += 3000;
        var pilot = fixture.Pilot;
        var health = new PilotHealthStore(new(fixture.Db.Path));
        CombatShot shot;
        using (var measure = SessionPerformance.Begin("warm objects", _ => { }, force: true)!)
        {
            shot = fixture.Combat.Fire(
                pilot.PilotId,
                fixture.Gun,
                fixture.Npc.Id,
                SpacePosition.Zero,
                fixture.Clock.Now,
                0.5,
                WeaponActivation.AmmoFed
            );
            Assert.Equal(198, shot.Magazine!.Quantity);
            Assert.True(shot.Applied > 0);
            for (int read = 0; read < 10; read++)
            {
                Assert.Equal(pilot.ShipId, health.Read(pilot.PilotId, fixture.Clock.Now).ShipId);
            }
            Assert.Equal(0, measure.Commands);
            Assert.Equal(0, measure.Connections);
            Assert.Equal(0, measure.FittingCalculations);
        }
        var live = LiveWorld.Find(fixture.Db.Path)!;
        await live.FlushAsync();
        string checkpoint = Path.Combine(Path.GetDirectoryName(fixture.Db.Path)!, "combat-checkpoint.sqlite");
        File.Copy(fixture.Db.Path, checkpoint);
        using WorldDbContext projected = new(checkpoint);
        Assert.Equal(198, projected.Set<OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
        Assert.Equal(shot.Capacitor, projected.Set<ShipEnergy>().Find(pilot.ShipId)!.Charge);
        Assert.Equal(
            fixture.Clock.Now + (long)Math.Ceiling(shot.Weapon.CycleMilliseconds * TimeSpan.TicksPerMillisecond),
            projected.Set<ModuleCooldown>().Find(fixture.Gun)!.ReadyAt
        );
        Assert.Equal(shot.Health.Remaining.Shield, projected.Set<WorldNpc>().Find(fixture.Npc.Id)!.ShieldCharge);
        Assert.NotEmpty(projected.Set<SavedCombatDamage>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmSingleAndGroupedVolleysStaySqlFreeAfterSharedMotion(bool grouped)
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        if (grouped)
        {
            long second = fixture.Buy(561, 1);
            fixture.Fitting.Fit(
                fixture.Pilot.PilotId,
                [second],
                fixture.Pilot.StationId,
                (int)Eden.Gameplay.Inventory.InventoryFlag.AutoFit
            );
            fixture.Fitting.SetOnline(fixture.Pilot.PilotId, fixture.Pilot.ShipId, second, true);
            new PilotWeaponBankStore(new(fixture.Db.Path), fixture.Clock).Change(
                fixture.Pilot.PilotId,
                fixture.Pilot.ShipId,
                WeaponBankOperation.Link,
                fixture.Gun,
                second
            );
        }
        Ready(fixture);
        var pilot = fixture.Pilot;
        using var context = fixture.Context();
        context.Flight.Snapshot();
        CombatVolley Fire() =>
            fixture.Combat.FireBank(
                pilot.PilotId,
                fixture.Gun,
                fixture.Npc.Id,
                SpacePosition.Zero,
                fixture.Clock.Now,
                0.5,
                WeaponActivation.AmmoFed
            );
        _ = Fire();
        var encounters = fixture.Db.Services.GetRequiredService<INpcEncounters>();
        _ = encounters.List(fixture.Npc.SystemId, fixture.Clock.Now);
        fixture.Clock.Milliseconds += 3000;
        context.Flight.Checkpoint();
        using var measure = SessionPerformance.Begin("objects after motion", _ => { }, force: true)!;
        var volley = Fire();
        var visible = encounters.List(fixture.Npc.SystemId, fixture.Clock.Now).Single(n => n.Id == fixture.Npc.Id);
        Assert.Equal(volley.Shot.Health.Remaining.Shield, visible.Health.Remaining.Shield, 8);
        Assert.Equal(volley.Shot.Health.Remaining.Armor, visible.Health.Remaining.Armor);
        Assert.Equal(volley.Shot.Health.Remaining.Structure, visible.Health.Remaining.Structure);
        Assert.Equal(grouped ? 2 : 1, volley.Weapons.Count);
        Assert.All(volley.Magazines, magazine => Assert.Equal(198, magazine.Quantity));
        Assert.Equal(0, measure.Commands);
        Assert.Equal(0, measure.Connections);
        Assert.Equal(0, measure.FittingCalculations);
    }

    [Fact]
    public void DueNpcGunfireAndHealthReadsUseObjectsAndPayOnlyOneCycle()
    {
        using NpcCombatTests.Fixture fixture = new(liveWorld: true);
        fixture.Aggress();
        var pilot = fixture.Pilot;
        using Eden.Protocol.Flight.FlightSession flight = new(fixture.Db.Store, pilot, fixture.Clock, new(), fixture.Db.Objects);
        flight.Snapshot();
        fixture.Clock.Milliseconds += 7500;
        flight.Checkpoint();
        var combat = fixture.Combat;
        using var measure = SessionPerformance.Begin("incoming objects", _ => { }, force: true)!;
        var shot = Assert.IsType<NpcShot>(
            combat.Fire(pilot.PilotId, pilot.ShipId, fixture.Npc.Id, SpacePosition.Zero, fixture.Clock.Now, 0.5, flight.CurrentStamp)
        );
        Assert.Equal(2.475, shot.Applied, 8);
        Assert.Null(
            combat.Fire(pilot.PilotId, pilot.ShipId, fixture.Npc.Id, SpacePosition.Zero, fixture.Clock.Now, 0.5, flight.CurrentStamp)
        );
        Assert.Equal(shot.Health.Remaining, fixture.Health.Read(pilot.PilotId, fixture.Clock.Now).Remaining);
        Assert.Equal(0, measure.Commands);
        Assert.Equal(0, measure.Connections);
    }

    [Fact]
    public void ShutdownProjectsTheLastUnflushedVolleyBeforeReopeningTheFile()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        Ready(fixture);
        var shot = fixture.Fire();
        long ship = fixture.Pilot.ShipId;
        // Leave another payment exclusively in objects until the shutdown checkpoint.
        fixture.Clock.Milliseconds += 3000;
        shot = fixture.Combat.Fire(fixture.Pilot.PilotId, fixture.Gun, fixture.Npc.Id, SpacePosition.Zero, fixture.Clock.Now, 0.5);
        fixture.Db.Services.Dispose();
        Assert.Null(LiveWorld.Find(fixture.Db.Path));
        using WorldDbContext reopened = new(fixture.Db.Path);
        Assert.Equal(198, reopened.Set<OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
        Assert.Equal(shot.Capacitor, reopened.Set<ShipEnergy>().Find(ship)!.Charge);
        Assert.Equal(shot.Health.Remaining.Shield, reopened.Set<WorldNpc>().Find(fixture.Npc.Id)!.ShieldCharge);
        Assert.NotEmpty(reopened.Set<SavedCombatDamage>());
    }

    [Fact]
    public void SqlReadersAndReconfigurationSeePriorObjectPaymentsAndRollbackDoesNotLoseThem()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        Ready(fixture);
        var paid = fixture.Fire();
        using (WorldDbContext reader = new(fixture.Db.Path))
        {
            Assert.Equal(199, reader.Set<OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
        }
        using (WorldDbContext write = new(fixture.Db.Path))
        using (var transaction = write.Database.BeginTransaction())
        {
            write.Set<OwnedItem>().Find(fixture.Gun)!.Online = false;
            write.SaveChanges();
            transaction.Rollback();
        }
        fixture.Clock.Milliseconds += 3000;
        var next = fixture.Fire();
        Assert.Equal(198, next.Magazine!.Quantity);
        Assert.True(next.Capacitor < paid.Capacitor);
        using (WorldDbContext write = new(fixture.Db.Path))
        {
            write.Database.ExecuteSql($"UPDATE WorldItem SET Online=0 WHERE Id={fixture.Gun}");
        }
        fixture.Clock.Milliseconds += 3000;
        Assert.Throws<InvalidDataException>(() => fixture.Fire());
        Assert.Equal(198, fixture.Ammo.Read(fixture.Pilot.PilotId, fixture.Pilot.ShipId).Single().Quantity);
    }

    [Fact]
    public void ReplacedGunCannotReuseItsPreviousOwnersCalculatedProfile()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        Ready(fixture);
        _ = fixture.Fire();
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSql(
                $"""
                INSERT OR REPLACE INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity,Singleton,Online,CrystalDamage)
                SELECT Id,90000002,TypeId,LocationId,FlagId,Quantity,Singleton,Online,CrystalDamage FROM WorldItem WHERE Id={fixture.Gun}
                """
            );
        }
        fixture.Clock.Milliseconds += 3000;
        Assert.Throws<InvalidDataException>(() => fixture.Fire());
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Equal(199, saved.Set<OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
    }

    [Fact]
    public void RemovedNpcCannotReappearFromAnOlderObjectOnRepeatedFire()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        Ready(fixture);
        _ = fixture.Fire();
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSql($"DELETE FROM WorldNpcAggression WHERE NpcId={fixture.Npc.Id}");
            world.Database.ExecuteSql($"DELETE FROM WorldNpc WHERE Id={fixture.Npc.Id}");
        }
        fixture.Clock.Milliseconds += 3000;
        Assert.Throws<InvalidDataException>(() => fixture.Fire());
        Assert.Throws<InvalidDataException>(() => fixture.Fire());
        Assert.Equal(199, fixture.Ammo.Read(fixture.Pilot.PilotId, fixture.Pilot.ShipId).Single().Quantity);
    }

    [Fact]
    public void RejectedObjectShotLeavesAmmunitionResourcesAndTargetUnchanged()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        Ready(fixture);
        var first = fixture.Fire();
        Assert.Throws<InvalidDataException>(() => fixture.Fire());
        using WorldDbContext world = new(fixture.Db.Path);
        Assert.Equal(199, world.Set<OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
        Assert.Equal(first.Capacitor, world.Set<ShipEnergy>().Find(fixture.Pilot.ShipId)!.Charge);
        Assert.Equal(first.Health.Remaining.Shield, world.Set<WorldNpc>().Find(fixture.Npc.Id)!.ShieldCharge);
    }
}
