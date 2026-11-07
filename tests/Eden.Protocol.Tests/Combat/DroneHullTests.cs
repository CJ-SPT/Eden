using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Content;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Drones;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class DroneHullTests
{
    private const int SystemId = 30000144;
    private const int Warrior = 2488;
    private const int Hammerhead = 2185;
    private const int Berserker = 2476;
    private const int Garde = 23561;
    private const int GuardianVexor = 11011;
    private const int GallenteCruiser = 3332;

    private static void Prepare(Fixture fixture, int hull, int level = 3)
    {
        PassiveCatalogFixture.LoadDroneHull(fixture.Base.CatalogPath, hull);
        using CatalogDbContext catalog = new(fixture.Base.CatalogPath);
        foreach (int skill in HullCatalog.Skills(catalog, hull))
        {
            fixture.Base.Train(skill, level);
        }
        using WorldDbContext world = new(fixture.Base.Db.Path);
        world.Ships.Find(fixture.Ship)!.TypeId = hull;
        world.SaveChanges();
    }

    [Theory]
    [InlineData(626, Warrior, 1.3, 1.3, 1, 1, 1)]
    [InlineData(626, Hammerhead, 1.3, 1.3, 1, 1, 1)]
    [InlineData(626, Berserker, 1.3, 1.3, 1, 1, 1)]
    [InlineData(626, Garde, 1.3, 1.3, 1, 1, 1)]
    [InlineData(593, Warrior, 1, 1.3, 1.3, 1, 1)]
    [InlineData(645, Garde, 1.3, 1.3, 1.225, 1.225, 1)]
    [InlineData(17930, Warrior, 4, 4, 1, 1, 1)]
    [InlineData(17930, Hammerhead, 1, 1, 1, 1, 1)]
    [InlineData(17715, Hammerhead, 6, 3.5, 1, 1, 1)]
    [InlineData(17715, Warrior, 1, 1, 1, 1, 1)]
    [InlineData(17918, Berserker, 3.75, 3.75, 1, 1, 1)]
    [InlineData(17918, Garde, 3.75, 3.75, 1, 1, 1)]
    [InlineData(17918, Hammerhead, 1, 1, 1, 1, 1)]
    [InlineData(24700, Hammerhead, 1.3, 1.3, 1, 1, 1.125)]
    [InlineData(37455, Warrior, .3, .5, 1, 1, 1)]
    public void HullBonusesReachDronesWithoutRigsAndRespectOperationSize(
        int hull,
        int drone,
        double damage,
        double health,
        double tracking,
        double optimal,
        double speed
    )
    {
        using Fixture fixture = new();
        Prepare(fixture, hull);
        using WorldDbContext world = new(fixture.Base.Db.Path);
        using CatalogDbContext catalog = new(fixture.Base.CatalogPath);
        var attributes = catalog.FittingAttributes(drone);
        var required = InventoryDefinitions.RequiredSkills.Select(pair => (int)attributes.GetValueOrDefault(pair.Skill)).ToHashSet();
        var basis = DroneRules.ApplySkills(
            DroneCatalog.Profile(catalog, drone),
            DroneCatalog.Skills(world, catalog, fixture.Pilot),
            required
        );
        var actual = DroneCatalog.Skilled(world, catalog, fixture.Pilot, drone);
        Assert.Equal(basis.Damage.Total * damage, actual.Damage.Total, 8);
        Assert.Equal(basis.Defense.Shield * health, actual.Defense.Shield, 8);
        Assert.Equal(basis.Defense.Armor * health, actual.Defense.Armor, 8);
        Assert.Equal(basis.Defense.Structure * health, actual.Defense.Structure, 8);
        Assert.Equal(basis.Tracking * tracking, actual.Tracking, 8);
        Assert.Equal(basis.Optimal * optimal, actual.Optimal, 8);
        Assert.Equal(basis.MaximumSpeed * speed, actual.MaximumSpeed, 3);
    }

    [Fact]
    public void AllAdmittedDroneBayHullsAssembleLaunchAndRecallWithRealLimits()
    {
        using Fixture fixture = new();
        PassiveCatalogFixture.LoadDroneHulls(fixture.Base.CatalogPath);
        Eden.Protocol.Tests.Persistence.SmallHullTests.TrainCatalog(fixture.Base);
        using CatalogDbContext catalog = new(fixture.Base.CatalogPath);
        var hulls = catalog
            .Set<Eden.Data.Catalog.ItemType>()
            .Where(type => type.Group.CategoryId == (int)InventoryCategory.Ship)
            .AsEnumerable()
            .Where(type =>
                HullCatalog.CanAssemble(catalog, type, (int)InventoryCategory.Ship)
                && catalog.FittingAttributes(type.Id).GetValueOrDefault(DogmaAttributeId.DroneCapacity) > 0
            )
            .Select(type => type.Id)
            .ToHashSet();
        Assert.Equal(149, hulls.Count);
        Assert.Equal(hulls.Order(), BuildContentPolicy.Current.DroneHullTypes.Order());
        var fleet = fixture.Base.Db.Services.GetRequiredService<IPilotFleet>();
        foreach (int hull in hulls.Order())
        {
            long ship = Assert.Single(fleet.Assemble(fixture.Pilot, [fixture.Base.Grant(hull)], null).ShipIds);
            fleet.Board(fixture.Pilot, ship, fixture.Ship);
            long id = Assert.Single(fixture.Launch(Warrior, 1));
            var contact = Assert.Single(fixture.Drones.List(SystemId, fixture.Now));
            Assert.Equal(fixture.Ship, contact.ShipId);
            Assert.Equal(hull == GuardianVexor ? 10 : 5, fixture.Drones.Control(fixture.Pilot).MaximumDrones);
            Assert.Equal(5, fixture.Drones.Control(fixture.Pilot).UsedBandwidth);
            fixture.Drones.Order(fixture.Pilot, [id], DroneOrder.ReturnToBay, null, new HashSet<long>(), fixture.Now);
            Assert.Equal(id, Assert.Single(fixture.Tick().Changes).Item.Id);
            fixture.Base.Db.Store.SetPosition(fixture.Ship, null);
            fixture.Base.Inventory.Move(fixture.Pilot, [id], fixture.Ship, fixture.Base.StationId, (int)InventoryFlag.Hangar, null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogControlBonusAllowsEightCommandsReleasesExcessAndReconnectsAfterRestart(bool resident)
    {
        using Fixture fixture = new();
        Prepare(fixture, GuardianVexor);
        using LiveWorld? live = resident ? new(new(fixture.Base.Db.Path), fixture.Base.Clock, _ => { }) : null;
        long extra = fixture.Bay(Warrior, 1);
        var ids = fixture.Launch(Warrior, 8);
        Assert.Equal(8, fixture.Drones.Control(fixture.Pilot).MaximumDrones);
        Assert.Throws<InvalidDataException>(() => fixture.Drones.Launch(fixture.Pilot, [new(extra, 1)], fixture.Now));
        using (WorldDbContext world = new(fixture.Base.Db.Path))
        {
            Assert.Equal(1, world.Set<OwnedItem>().Find(extra)!.Quantity);
            Assert.Equal(8, world.Set<WorldDrone>().Count());
        }
        fixture.Drones.Order(fixture.Pilot, ids, DroneOrder.Orbit, null, new HashSet<long>(), fixture.Now);
        fixture.Base.Train(GallenteCruiser, 1);
        fixture.Tick();
        var contacts = fixture.Drones.List(SystemId, fixture.Now);
        Assert.Equal(ids.Order().Take(6), contacts.Where(drone => drone.ShipId != 0).Select(drone => drone.Id).Order());
        fixture.Drones.Order(fixture.Pilot, ids.Order().Take(6).ToArray(), DroneOrder.Abandon, null, new HashSet<long>(), fixture.Now);
        fixture.Base.Train(GallenteCruiser, 3);
        using (var scope = fixture.Base.Db.Services.CreateScope())
        using (var context = DroneSynchronizationTests.Context(fixture, scope.ServiceProvider, 1000001))
        {
            context.Flight.Snapshot();
            var reply = scope.ServiceProvider.GetRequiredService<DroneHandler>().Call(context, "entity", "CmdReconnectToDrones", []);
            Assert.IsType<PilotCallResult.Success>(reply);
            Assert.Equal(8, fixture.Drones.List(SystemId, fixture.Now).Count(drone => drone.ShipId == fixture.Ship));
        }
        live?.Dispose();
        _ = WorldInitialization.Open(fixture.Base.Db.Path);
        Assert.All(fixture.Drones.List(SystemId, fixture.Now), drone => Assert.Equal(0, drone.ShipId));
        fixture.Drones.Order(fixture.Pilot, ids, DroneOrder.Reconnect, null, new HashSet<long>(), fixture.Now);
        fixture.Drones.Order(fixture.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), fixture.Now);
        Assert.Equal(ids.Order(), fixture.Tick().Changes.Select(change => change.Item.Id).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PercentageControlRangeIncludesSkillsAndSkillChangesRefreshBonusedProfiles(bool resident)
    {
        using Fixture fixture = new();
        Prepare(fixture, 37455);
        Assert.Equal(30_000, fixture.Drones.Control(fixture.Pilot).Range);
        fixture.Base.Train((int)DroneSkill.DroneAvionics, 1);
        Assert.Equal(20_000, fixture.Drones.Control(fixture.Pilot).Range);
        Prepare(fixture, 626);
        using LiveWorld? live = resident ? new(new(fixture.Base.Db.Path), fixture.Base.Clock, _ => { }) : null;
        fixture.Launch(Hammerhead, 2);
        fixture.Tick();
        var before = fixture.Drones.List(SystemId, fixture.Now);
        fixture.Base.Train(GallenteCruiser, 4);
        fixture.Tick();
        var after = fixture.Drones.List(SystemId, fixture.Now);
        Assert.All(
            after,
            drone => Assert.Equal(before.Single(old => old.Id == drone.Id).Profile.Damage.Total * 1.4 / 1.3, drone.Profile.Damage.Total, 8)
        );
        if (resident)
        {
            using var metrics = SessionPerformance.Begin("warm bonused drone frames", _ => { }, force: true);
            for (int frame = 0; frame < 12; frame++)
            {
                fixture.Tick();
                for (int observer = 0; observer < 10; observer++)
                {
                    Assert.Equal(
                        after.Select(drone => drone.Profile),
                        fixture.Drones.List(SystemId, fixture.Now).Select(drone => drone.Profile)
                    );
                }
            }
            Assert.Equal(0, metrics!.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
    }

    [Fact]
    public void CachedProfilesUseTheirControllerShipAndDiscardRolledBackSkillChanges()
    {
        using Fixture fixture = new();
        Prepare(fixture, 626);
        Prepare(fixture, 17715);
        Prepare(fixture, 626);
        using LiveWorld live = new(new(fixture.Base.Db.Path), fixture.Base.Clock, _ => { });
        long original = fixture.Ship;
        DroneProfile Profile(long ship)
        {
            using WorldDbContext world = new(fixture.Base.Db.Path);
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(fixture.Base.CatalogPath);
            var result = DroneCatalog.Skilled(world, catalog, fixture.Pilot, Hammerhead, ship);
            transaction.Commit();
            return result;
        }
        var before = Profile(original);
        var fleet = fixture.Base.Db.Services.GetRequiredService<IPilotFleet>();
        long next = Assert.Single(fleet.Assemble(fixture.Pilot, [fixture.Base.Grant(17715)], null).ShipIds);
        fleet.Board(fixture.Pilot, next, original);
        Assert.Equal(before, Profile(original));
        Assert.NotEqual(before.Damage, Profile(next).Damage);
        using (WorldDbContext world = new(fixture.Base.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(fixture.Base.CatalogPath))
        {
            double rank = catalog.FittingAttributes(GallenteCruiser)[DogmaAttributeId.SkillTimeConstant];
            world.Set<PilotSkill>().Find(fixture.Pilot, GallenteCruiser)!.Points = TrainedSkill.PointsForLevel(rank, 4);
            world.SaveChanges();
            var changed = DroneCatalog.Skilled(world, catalog, fixture.Pilot, Hammerhead, original);
            Assert.Equal(before.Damage.Total * 1.4 / 1.3, changed.Damage.Total, 8);
            transaction.Rollback();
        }
        Assert.Equal(before, Profile(original));
    }

    [Theory]
    [InlineData(GuardianVexor, 8, 60_000)]
    [InlineData(37455, 5, 30_000)]
    [InlineData(645, 5, 60_000)]
    [InlineData(593, 5, 60_000)]
    public void NativePrimesCarryEffectiveControlAndTrackingWithoutDuplicateBrainControl(int hull, int count, int range)
    {
        using Fixture fixture = new();
        Prepare(fixture, hull);
        long id = fixture.Bay(Warrior, 1);
        var item = Assert.Single(fixture.Base.Inventory.List(fixture.Pilot, fixture.Ship), item => item.Id == id);
        using var scope = fixture.Base.Db.Services.CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>();
        var pilot = fixture.Base.Db.Store.FindPilot(1000001)!;
        var character = builder.PrimePilot(pilot);
        Assert.Equal(count, character.Attributes[DogmaAttributeId.MaxActiveDrones]);
        Assert.Equal(range, character.Attributes[DogmaAttributeId.DroneControlDistance]);
        var brain = scope.ServiceProvider.GetRequiredService<PilotBrain>().Read(fixture.Pilot, fixture.Ship);
        Assert.DoesNotContain(
            brain.CharacterEffects.Concat(brain.ShipEffects),
            effect => effect.Attribute is DogmaAttributeId.MaxActiveDrones or DogmaAttributeId.DroneControlDistance
        );
        var profile = fixture.Drones.Profile(fixture.Pilot, Warrior)!;
        var prime = builder.PrimeModule(pilot, item);
        Assert.Equal(profile.Tracking, prime.Attributes[DogmaAttributeId.TrackingSpeed], 8);
        Assert.Equal(profile.Falloff, prime.Attributes[DogmaAttributeId.AccuracyFalloff], 8);
        Assert.Equal(profile.Defense.Shield, prime.Attributes[DogmaAttributeId.ShieldCapacity], 8);
    }
}
