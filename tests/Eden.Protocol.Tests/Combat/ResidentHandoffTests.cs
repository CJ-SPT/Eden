using Eden.Data.World.Docking;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Ammunition;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class ResidentHandoffTests
{
    [Fact]
    public void DemotionPublishesDirtyMotionWithoutSqlAndRejectedPublicationRetainsIt()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        fixture.Load(quantity: 400);
        fixture.Clock.Milliseconds += 5000;
        fixture.Space();
        var pilot = fixture.Pilot;
        var live = LiveWorld.Find(fixture.Db.Path)!;
        live.Economy.Warm();
        _ = new PilotHealthStore(new(fixture.Db.Path)).Read(pilot.PilotId, fixture.Clock.Now);
        using var ownership = live.EnterShared("resident handoff test");
        using var context = live.UseSystem(fixture.Npc.SystemId);
        using var system = live.Enter("resident handoff warmup");
        var runtime = live.Combat!;
        Assert.False(runtime.HasDetailedInteraction(fixture.Clock.Now));
        var position = pilot.Position! + new SpacePosition(125, 25, -10);
        runtime.PublishPilotPosition(pilot.ShipId, pilot.SystemId, position);
        var pending = runtime.CaptureCheckpoint();
        Assert.Contains(pending.Positions, p => p.ShipId == pilot.ShipId && p.Position == position);
        var invalid = pending with { Positions = pending.Positions.Select(p => p with { Position = new(double.NaN, 0, 0) }).ToArray() };
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident system demotion", _ => { }, force: true, command: commands.Add)!)
        {
            Assert.Throws<InvalidDataException>(() => live.Economy.PublishCombat(invalid));
            Assert.Equal(position, runtime.InformationPosition(pilot.ShipId));
            Assert.True(runtime.CaptureCheckpoint().HasChanges);
            Assert.True(live.ReleaseSystemDetails(pilot.SystemId));
            Assert.False(runtime.CaptureCheckpoint().HasChanges);
            Assert.Equal(position, runtime.InformationPosition(pilot.ShipId));
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using var saved = new WorldDbContext(checkpoint.Database);
        var reopened = saved.Positions.Find(pilot.ShipId)!;
        Assert.Equal(position, new(reopened.X, reopened.Y, reopened.Z));
        Assert.Equal(pilot.SystemId, reopened.SystemId);
    }

    [Fact]
    public void PublishingPaidDroneCombatIsSqlFreeAndRollbackRetainsTheWholeGeneration()
    {
        using CombatDroneTests.Fixture fixture = new();
        using (var catalog = new Eden.World.Catalog.CatalogDbContext(fixture.Base.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000000 WHERE ItemTypeId=30380 AND AttributeId=263");
        }
        using (WorldDbContext world = new(fixture.Base.Db.Path))
        {
            world.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(fixture.Npc.Id)!.ShieldCharge = 1000000000;
            world.SaveChanges();
        }
        using LiveWorld live = new(new(fixture.Base.Db.Path), fixture.Base.Clock, _ => { });
        live.Economy.Warm();
        var drones = fixture.Launch(quantity: 1);
        fixture.Drones.Order(
            fixture.Pilot,
            drones,
            Eden.Gameplay.Drones.DroneOrder.Engage,
            fixture.Npc.Id,
            new HashSet<long> { fixture.Npc.Id },
            fixture.Now
        );
        _ = fixture.Tick(2);
        _ = fixture.Tick(2);
        using var ownership = live.EnterShared("paid drone handoff test");
        var runtime = live.Combat!;
        var pending = runtime.CaptureCheckpoint();
        Assert.NotEmpty(pending.Drones);
        Assert.NotEmpty(pending.Npcs);
        var expected = Assert.Single(pending.Drones).Contact;
        var before = live.Economy.Peek<Eden.Data.World.Drones.WorldDrone>(expected.Id)!;
        var invalid = pending with
        {
            Drones = pending.Drones.Select(d => d with { Contact = d.Contact with { Position = new(double.NaN, 0, 0) } }).ToArray(),
        };
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident paid drone publication", _ => { }, force: true, command: commands.Add)!)
        {
            Assert.Throws<InvalidDataException>(() => live.Economy.PublishCombat(invalid));
            Assert.NotEmpty(runtime.CaptureCheckpoint().Drones);
            Assert.Equal(before.UpdatedAt, live.Economy.Peek<Eden.Data.World.Drones.WorldDrone>(expected.Id)!.UpdatedAt);
            live.Economy.PublishCombat(pending);
            Assert.False(runtime.CaptureCheckpoint().HasChanges);
            _ = fixture.Tick(2);
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        var latest = runtime.CaptureCheckpoint();
        expected = latest.Drones.SingleOrDefault(d => d.Contact.Id == expected.Id)?.Contact ?? expected;
        using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        captured.Project();
        using var saved = new WorldDbContext(captured.Database);
        var drone = saved.Set<Eden.Data.World.Drones.WorldDrone>().Find(expected.Id)!;
        Assert.Equal(expected.Position, new(drone.X, drone.Y, drone.Z));
        Assert.Equal(expected.Velocity, new(drone.VelocityX, drone.VelocityY, drone.VelocityZ));
        Assert.Equal(expected.TargetId, drone.TargetId);
        Assert.Equal(expected.ShotAt, drone.ShotAt);
        foreach (var npc in latest.Npcs)
        {
            var row = saved.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(npc.After.Id)!;
            Assert.Equal(npc.After.Health.Remaining.Shield, row.ShieldCharge);
        }
        Assert.NotEmpty(saved.Set<Eden.Data.World.Characters.SavedCombatDamage>());
    }

    [Fact]
    public void ScopedNpcPublicationPreservesOtherDirtyShotsAndAggression()
    {
        using AmmunitionTests.Fixture fixture = new(liveWorld: true);
        fixture.Load(quantity: 400);
        fixture.Clock.Milliseconds += 5000;
        fixture.Space();
        var pilot = fixture.Pilot;
        var live = LiveWorld.Find(fixture.Db.Path)!;
        var contacts = fixture.Db.Services.GetRequiredService<INpcEncounters>().List(fixture.Npc.SystemId, fixture.Clock.Now);
        long other = contacts.First(n => n.Id != fixture.Npc.Id).Id;
        using (var setup = live.Economy.Begin(new(resources: [new(SharedSimulationResource.Npc, other)])))
        {
            var npc = setup.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(other)!;
            var position = fixture.Npc.Position + new SpacePosition(0, 500, 0);
            (npc.X, npc.Y, npc.Z) = (position.X, position.Y, position.Z);
            (npc.HomeX, npc.HomeY, npc.HomeZ) = (position.X, position.Y, position.Z);
            setup.Commit();
        }
        var first = fixture.Combat.Fire(pilot.PilotId, fixture.Gun, fixture.Npc.Id, SpacePosition.Zero, fixture.Clock.Now, .5);
        fixture.Clock.Milliseconds += 3000;
        var second = fixture.Combat.Fire(pilot.PilotId, fixture.Gun, other, SpacePosition.Zero, fixture.Clock.Now, .5);
        Assert.False(first.Destroyed);
        Assert.False(second.Destroyed);
        using var ownership = live.EnterShared("scoped NPC publication test");
        using var context = live.UseSystem(pilot.SystemId);
        var runtime = live.Combat!;
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("scoped resident NPC publication", _ => { }, force: true, command: commands.Add)!)
        {
            using (var action = live.Economy.Begin(new(resources: [new(SharedSimulationResource.Npc, fixture.Npc.Id)])))
            {
                action.Commit();
            }
            var pending = runtime.CaptureCheckpoint();
            Assert.Contains(pending.Npcs, n => n.After.Id == other);
            Assert.Contains(pending.Aggressions, a => a.NpcId == other);
            _ = runtime.RattingNpc(other, fixture.Clock.Now);
            _ = new PilotHealthStore(new(fixture.Db.Path)).Read(pilot.PilotId, fixture.Clock.Now);
            var retained = runtime.CaptureCheckpoint();
            var ship = Assert.Single(retained.Ships);
            Assert.Equal(198, ship.After.Turrets[fixture.Gun].Quantity);
            Assert.NotEqual(ship.Before.Turrets[fixture.Gun].ReadyAt, ship.After.Turrets[fixture.Gun].ReadyAt);
            var npc = Assert.Single(retained.Npcs);
            Assert.Equal(other, npc.After.Id);
            Assert.NotEqual(npc.Before.Health, npc.After.Health);
            Assert.Equal(2, runtime.Aggressors(pilot.ShipId)!.Count);
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
        }
        using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        captured.Project();
        using var saved = new WorldDbContext(captured.Database);
        Assert.Equal(first.Health.Remaining.Shield, saved.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(fixture.Npc.Id)!.ShieldCharge);
        Assert.Equal(second.Health.Remaining.Shield, saved.Set<Eden.Data.World.Combat.Npcs.WorldNpc>().Find(other)!.ShieldCharge);
        Assert.Equal(198, saved.Set<Eden.Data.World.Inventory.OwnedItem>().Single(i => i.LocationId == fixture.Gun).Quantity);
        Assert.Equal(2, saved.Set<Eden.Data.World.Combat.NpcAggression>().Count());
    }
}
