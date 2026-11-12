using Eden.Data.Catalog;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Drones;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class ResidentDroneTests
{
    [Fact]
    public async Task UnrelatedClassifiedInventoryCommitPreservesDirtyDroneFlightAndWarmObservers()
    {
        using Fixture f = new();
        using LiveWorld live = new(f.Base.Db.Services.GetRequiredService<WorldDatabase>(), f.Base.Clock, _ => { });
        f.Launch();
        f.Tick();
        var expected = f.Drones.List(30000144, f.Now).ToArray();
        using (WorldDbContext world = new(f.Base.Db.Path) { Participants = TransactionParticipants.None })
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Add(
                new OwnedItem
                {
                    Id = 8000000000001,
                    OwnerId = f.Pilot,
                    TypeId = 34,
                    LocationId = world.Ships.Single(s => s.Id == f.Ship).StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 1,
                }
            );
            world.SaveChanges();
            transaction.Commit();
        }
        using (var metrics = SessionPerformance.Begin("remote inventory retains drone flight", _ => { }, force: true)!)
        {
            Assert.Equal(expected, f.Drones.List(30000144, f.Now));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Equal(expected.Length, live.Combat!.CaptureCheckpoint().Drones.Count);
        await live.FlushAsync();
        string checkpoint = Path.Combine(Path.GetDirectoryName(f.Base.Db.Path)!, "drone-participants.sqlite");
        File.Copy(f.Base.Db.Path, checkpoint);
        using WorldDbContext saved = new(checkpoint);
        Assert.Equal(
            expected.Select(d => d.Position),
            saved.Set<WorldDrone>().OrderBy(d => d.ItemId).Select(d => new SpacePosition(d.X, d.Y, d.Z)).ToArray()
        );
    }

    [Fact]
    public void WarmObserversAndControllerChecksShareContactsAndSeeCommittedOrders()
    {
        using Fixture f = new();
        var database = f.Base.Db.Services.GetRequiredService<WorldDatabase>();
        using CatalogLifetime catalog = new(database);
        using LiveWorld live = new(database, f.Base.Clock, _ => { });
        var ids = f.Launch();
        long ship = f.Ship;
        var contacts = f.Drones.List(30000144, f.Now);
        Assert.True(f.Drones.HasControlled(f.Pilot, ship));
        using (var metrics = SessionPerformance.Begin("warm drones", _ => { }, force: true))
        {
            for (int observer = 0; observer < 10; observer++)
            {
                Assert.Equal(contacts, f.Drones.List(30000144, f.Now));
                Assert.True(f.Drones.HasControlled(f.Pilot, ship));
                Assert.False(f.Drones.HasControlled(f.Pilot + 1, ship));
            }
            Assert.Equal(0, metrics!.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident drone recall", _ => { }, force: true, command: commands.Add)!)
        {
            f.Drones.Order(f.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Now);
            Assert.All(f.Drones.List(30000144, f.Now), d => Assert.Equal(DroneActivity.ReturningToBay, d.Activity));
            f.Tick();
            Assert.Empty(f.Drones.List(30000144, f.Now));
            Assert.False(f.Drones.HasControlled(f.Pilot, ship));
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
        }
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext saved = new(checkpoint.Database);
        Assert.All(ids, id => Assert.Equal(ship, saved.Set<OwnedItem>().Find(id)!.LocationId));
    }

    [Fact]
    public async Task WarmFlightAndObserversUseObjectsAndCheckpointLatestPositions()
    {
        using Fixture f = new();
        DroneContact[] expected;
        using (LiveWorld live = new(f.Base.Db.Services.GetRequiredService<WorldDatabase>(), f.Base.Clock, _ => { }))
        {
            f.Launch();
            f.Tick();
            var before = f.Drones.List(30000144, f.Now).ToArray();
            using (var metrics = SessionPerformance.Begin("warm flight", _ => { }, force: true))
            {
                for (int frame = 0; frame < 12; frame++)
                {
                    f.Tick();
                    for (int observer = 0; observer < 10; observer++)
                    {
                        Assert.Equal(5, f.Drones.List(30000144, f.Now).Count);
                    }
                }
                Assert.Equal(0, metrics!.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            expected = f.Drones.List(30000144, f.Now).ToArray();
            Assert.NotEqual(before[0].Position, expected[0].Position);
            await live.FlushAsync();
        }
        Assert.Equal(expected, f.Drones.List(30000144, f.Now));
        WorldInitialization.Initialize(new(f.Base.Db.Path), f.Base.Clock);
        Assert.All(f.Drones.List(30000144, f.Now), d => Assert.Equal(0, d.ShipId));
        Assert.Equal(expected.Select(d => d.Position), f.Drones.List(30000144, f.Now).Select(d => d.Position));
    }

    [Fact]
    public void WarmShotsRetaliationAndDuplicateCallbacksKeepPaidDeadlinesWithoutSql()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000000 WHERE AttributeId=263");
        }
        using (WorldDbContext world = new(f.Base.Db.Path))
        {
            world.Set<WorldNpc>().ExecuteUpdate(set => set.SetProperty(n => n.ShieldCharge, 1000000000d));
        }
        using LiveWorld live = new(f.Base.Db.Services.GetRequiredService<WorldDatabase>(), f.Base.Clock, _ => { });
        var ids = f.Launch();
        f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, f.Npc.Id, new HashSet<long> { f.Npc.Id }, f.Now);
        Assert.Equal(5, f.Tick().Hits.Count);
        var combat = f.Base.Db.Services.GetRequiredService<INpcCombat>();
        var database = f.Base.Db.Services.GetRequiredService<WorldDatabase>();
        long ship = f.Ship;
        NpcShot Fire(int stamp)
        {
            database.MotionStamps[30000144] = stamp;
            return combat.Fire(f.Pilot, ship, f.Npc.Id, SpacePosition.Zero, f.Now, 0.01, stamp)!;
        }
        f.Base.Clock.Seconds += 30;
        Assert.Contains(Fire(31).Health.ShipId, ids);
        f.Base.Clock.Seconds += 30;
        using (var metrics = SessionPerformance.Begin("warm combat", _ => { }, force: true))
        {
            Assert.Equal(5, f.Drones.Advance(f.Pilot, f.Now, 31).Hits.Count);
            Assert.Empty(f.Drones.Advance(f.Pilot, f.Now, 31).Hits);
            Assert.True(metrics!.Commands == 0, $"advance SQL: {metrics.Commands}");
            var shot = Fire(31);
            Assert.True(metrics.Commands == 0, $"retaliation SQL: {metrics.Commands}");
            Assert.Contains(shot.Health.ShipId, ids);
            Assert.False(shot.Health.Remaining.Destroyed);
            Assert.Null(combat.Fire(f.Pilot, ship, f.Npc.Id, SpacePosition.Zero, f.Now, 0.01, 31));
            Assert.Equal(0, metrics!.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
    }

    [Fact]
    public void ChangedSkillsInvalidateProfilesAndReleaseExcessDronesBeforeTheNextTick()
    {
        using Fixture f = new();
        using LiveWorld live = new(f.Base.Db.Services.GetRequiredService<WorldDatabase>(), f.Base.Clock, _ => { });
        f.Launch(type: 2486);
        f.Tick();
        f.Base.Train((int)DroneSkill.Drones, 2);
        Assert.True(f.Tick().ControlChanged);
        var drones = f.Drones.List(30000144, f.Now);
        Assert.Equal(2, drones.Count(d => d.ShipId != 0));
        Assert.Equal(3, drones.Count(d => d.ShipId == 0 && d.Velocity == SpacePosition.Zero));
    }

    [Fact]
    public async Task ReleaseProjectsLatestMovementAndRolledBackDamageCannotLeakIntoScoop()
    {
        using Fixture f = new();
        using LiveWorld live = new(f.Base.Db.Services.GetRequiredService<WorldDatabase>(), f.Base.Clock, _ => { });
        long id = Assert.Single(f.Launch(quantity: 1));
        f.Tick();
        var latest = Assert.Single(f.Drones.List(30000144, f.Now));
        long ship = f.Ship;
        f.Drones.Release(f.Pilot, ship);
        Assert.Equal(latest.Position, Assert.Single(f.Drones.List(30000144, f.Now)).Position);
        using (WorldDbContext world = new(f.Base.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Set<WorldDrone>().Find(id)!.Shield = 0;
            world.SaveChanges();
            transaction.Rollback();
        }
        Assert.Equal(latest.Health, Assert.Single(f.Drones.List(30000144, f.Now)).Health);
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            return f.Drones.Scoop(f.Pilot, [id], InventoryFlag.DroneBay, f.Now).Count;
                        }
                        catch (InvalidDataException)
                        {
                            return 0;
                        }
                    })
                )
        );
        Assert.Equal(1, results.Count(result => result > 0));
        Assert.Empty(f.Drones.List(30000144, f.Now));
        await live.FlushAsync();
        using WorldDbContext saved = new(f.Base.Db.Path);
        var item = saved.Set<OwnedItem>().Find(id)!;
        Assert.Equal(ship, item.LocationId);
        Assert.Equal((int)InventoryFlag.DroneBay, item.FlagId);
    }
}
