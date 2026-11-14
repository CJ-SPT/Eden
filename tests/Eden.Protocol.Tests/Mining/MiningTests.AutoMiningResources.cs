using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class MiningTests
{
    [Fact]
    public void AutoMiningApproachesOnlyWhenEnabledAndYieldsToManualNavigation()
    {
        using Fixture f = new(useTestClock: true);
        var (context, targeting, mining, module) = f.Client(locked: false);
        PlaceRock(f, context, 11_000);
        var controller = CreateController(f, targeting, mining);
        Assert.Equal("Waiting for matching ore in range", Run(controller, context, "Start").Status);
        Assert.DoesNotContain(context.Flight.Updates, update => update.Command is DestinyCommand.Follow);

        _ = Run(controller, context, "SaveOptions", (long)MiningFamily.Ore, (long)AutoMiningTargetOrder.Nearest, true, false);
        controller.Advance(context);
        Assert.Equal("Approaching asteroid", Run(controller, context, "GetStatus").Status);
        Assert.Contains(context.Flight.Updates, update => update.Command is DestinyCommand.Follow);
        Assert.False(targeting.IsPending(Fixture.Asteroid));
        double initialDistance = context.Pilot.Position!.X;
        for (int second = 0; second < 60 && !mining.IsAutomatic(module); second++)
        {
            f.Clock.Milliseconds += 1000;
            context.Flight.Checkpoint();
            targeting.Advance(context);
            controller.Advance(context);
        }
        Assert.True(mining.IsAutomatic(module));
        Assert.True(context.Pilot.Position!.X > initialDistance);
        Assert.Null(context.Flight.CallWire("beyonce", "CmdStop", []));
        Assert.False(Run(controller, context, "GetStatus").Running);
        Assert.False(mining.IsActive(module));
    }

    [Theory]
    [InlineData(AutoMiningTargetOrder.Largest, 1)]
    [InlineData(AutoMiningTargetOrder.Smallest, 0)]
    public void AutoMiningRanksFreshResidentSurveyVolumeAndRequiresTheChipset(AutoMiningTargetOrder order, int offset)
    {
        using Fixture f = new(useTestClock: true);
        AddAutoMiningRocks(f, 500);
        string path = new WorldDatabase(f.Db.Path).CatalogPath;
        PassiveCatalogFixture.LoadInformation(path);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        long chipset;
        using (WorldDbContext world = new(f.Db.Path))
        {
            chipset = InventoryPersistence.NextId(world);
            world.Add(
                new OwnedItem
                {
                    Id = chipset,
                    OwnerId = pilot.PilotId,
                    LocationId = pilot.ShipId,
                    FlagId = (int)InventoryFlag.MediumSlot0,
                    TypeId = 444,
                    Singleton = true,
                    Quantity = 1,
                }
            );
            world.SaveChanges();
        }
        var (context, targeting, mining, module) = f.Client(locked: false);
        var controller = CreateController(f, targeting, mining);
        _ = Run(controller, context, "SaveOptions", (long)MiningFamily.Ore, (long)order, false, false);
        Assert.Contains("online mining survey chipset", Run(controller, context, "Start").Status);
        Assert.False(targeting.IsPending(Fixture.Asteroid));
        using (var world = LiveWorld.Find(f.Db.Path)!.Economy.Begin(new(pilots: [pilot.PilotId])))
        {
            world.Set<OwnedItem>().Find(chipset)!.Online = true;
            world.Set<WorldAsteroid>().Find(Fixture.Asteroid + 1)!.Quantity = 1200;
            world.Commit();
        }
        f.Clock.Milliseconds += 4000;
        controller.Advance(context);
        Assert.True(targeting.IsPending(Fixture.Asteroid + offset));
        f.Clock.Milliseconds += 10_000;
        targeting.Advance(context);
        controller.Advance(context);
        Assert.Equal(Fixture.Asteroid + offset, mining.ActiveEffect(context.Pilot, module)!.TargetId);
    }

    [Theory]
    [InlineData(MiningFamily.Ice, 17478, 16281, 16278, 16265, 157500, DogmaEffect.MiningLaser)]
    [InlineData(MiningFamily.Gas, 601, 25544, 25266, 28694, 30000, DogmaEffect.MiningClouds)]
    public void AutoMiningHarvestsIceAndGasThroughNativeCycles(
        MiningFamily family,
        int hull,
        int skillId,
        int harvester,
        int resource,
        int duration,
        DogmaEffect effect
    )
    {
        using Fixture f = new(useTestClock: true);
        string path = new WorldDatabase(f.Db.Path).CatalogPath;
        PassiveCatalogFixture.LoadMining(path);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using (CatalogDbContext catalog = new(path))
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(pilot.ShipId)!.TypeId = hull;
            double rank = catalog.Set<Eden.Data.Catalog.TypeAttribute>().Find(skillId, (int)DogmaAttributeId.SkillTimeConstant)!.Value;
            world.Add(
                new PilotSkill
                {
                    PilotId = pilot.PilotId,
                    TypeId = skillId,
                    Points = TrainedSkill.PointsForLevel(rank, 5),
                }
            );
            world.Set<WorldAsteroid>().Find(Fixture.Asteroid)!.TypeId = resource;
            world.SaveChanges();
        }

        var (context, targeting, mining, module) = f.Client(locked: false, laser: (MiningLaserType)harvester);
        PlaceRock(f, context, 100);
        var controller = CreateController(f, targeting, mining);
        _ = Run(controller, context, "SaveFilter", "Scordite");
        Assert.IsType<PilotCallResult.Failure>(controller.Call(context, AutoMiningHandler.Service, "Start", []));
        _ = Run(controller, context, "SaveOptions", (long)family, (long)AutoMiningTargetOrder.Nearest, false, false);
        Assert.True(Run(controller, context, "Start").Running);
        f.Clock.Milliseconds += 10_000;
        targeting.Advance(context);
        controller.Advance(context);
        Assert.Equal(effect, mining.ActiveEffect(context.Pilot, module)!.Effect);
        f.Clock.Milliseconds += duration;
        mining.Advance(context);
        var harvest = Assert.Single(context.Notifications.OfType<OreMined>()).Yield;
        Assert.Equal(resource, harvest.TypeId);
        Assert.Equal(1, harvest.Extracted);
        Assert.Equal(10_000 - harvest.Extracted, f.Remaining());
        var effectName = family == MiningFamily.Gas ? MiningWireDefinitions.GasEffectName : MiningWireDefinitions.EffectName;
        Assert.Null(mining.CallWire(context, "dogmaIM", "Deactivate", [module, effectName]));
        controller.Advance(context);
        Assert.False(mining.IsActive(module));
    }

    [Fact]
    public async Task AutoMiningJettisonSplitsOnlyHarvestedCargoAndPersistsTheSharedCooldown()
    {
        using Fixture f = new(useTestClock: true);
        var (context, targeting, mining, _) = f.Client(locked: false);
        var pilot = context.Pilot;
        var controller = CreateController(f, targeting, mining);
        var live = LiveWorld.Find(f.Db.Path)!;
        var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
        var native = f.Db.Services.GetRequiredService<ICargoJettison>();
        const int oreUnits = 550_025;
        long ore;
        long other;
        using (var world = live.Economy.Begin(new(pilots: [pilot.PilotId], ships: [pilot.ShipId])))
        {
            ore = Seed(1230, oreUnits);
            other = Seed(3651, 1);
            world.Commit();

            long Seed(int type, int quantity) =>
                world
                    .Add(
                        new OwnedItem
                        {
                            Id = world.NextItemId(),
                            OwnerId = pilot.PilotId,
                            LocationId = pilot.ShipId,
                            FlagId = (int)InventoryFlag.Cargo,
                            TypeId = type,
                            Quantity = quantity,
                        }
                    )
                    .Entity.Id;
        }
        _ = Run(controller, context, "SaveHauling", (long)pilot.Home.Id, 1L);
        _ = Run(controller, context, "SaveOptions", (long)MiningFamily.Ore, (long)AutoMiningTargetOrder.Nearest, false, true);
        Assert.True(Run(controller, context, "Start").Running);
        var hauling = f.Db.Services.GetRequiredService<IPilotAutoMiningHauling>();
        _ = hauling.ReadHold(pilot.PilotId, pilot.ShipId);
        using (var metrics = SessionPerformance.Begin("automatic jettison", _ => { }, force: true)!)
        {
            controller.Advance(context, sessionTick: true);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        Assert.False(controller.IsHauling);
        var can = Assert.Single(context.Flight.VisibleWrecks);
        Assert.Contains(context.Flight.Updates, update => update.Command is DestinyCommand.AddBalls);
        Assert.Equal(550_000, Assert.Single(wrecks.Read(pilot.PilotId, can.Id, f.Clock.Now).Items).Quantity);
        Assert.Equal(25, f.Inventory.List(pilot.PilotId, pilot.ShipId).Single(item => item.Id == ore).Quantity);
        Assert.Equal(pilot.ShipId, f.Inventory.List(pilot.PilotId, pilot.ShipId).Single(item => item.Id == other).LocationId);
        Assert.Equal(JetcanRules.Cooldown, native.Jettison(pilot.PilotId, pilot.ShipId, [ore], f.Clock.Now).RetryAfter);
        f.Clock.Milliseconds += 1000;
        controller.Advance(context, sessionTick: true);
        Assert.Equal("Waiting for the jettison cooldown", Run(controller, context, "GetStatus").Status);
        Assert.Single(wrecks.List(pilot.SystemId, f.Clock.Now));

        await live.FlushAsync();
        f.Disconnect(context);
        live.Dispose();
        WorldDatabase database = new(f.Db.Path);
        WorldInitialization.Initialize(database, f.Clock);
        using var reopened = new LiveWorld(database, f.Clock);
        Assert.Equal(can.Id, Assert.Single(wrecks.List(pilot.SystemId, f.Clock.Now)).Id);
        Assert.Equal(550_000, Assert.Single(wrecks.Read(pilot.PilotId, can.Id, f.Clock.Now).Items).Quantity);
        Assert.Equal(
            f.Clock.Now - TimeSpan.TicksPerSecond + JetcanRules.Cooldown,
            reopened.Economy.Inspect(world => world.Set<SavedJettisonCooldown>().Find(pilot.PilotId)!.ReadyAt)
        );
        using (var metrics = SessionPerformance.Begin("resident jetcan expiry", _ => { }, force: true)!)
        {
            Assert.Empty(wrecks.List(pilot.SystemId, f.Clock.Now - TimeSpan.TicksPerSecond + JetcanRules.Lifetime));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        Assert.Empty(reopened.Economy.Inspect(world => world.Filter<OwnedItem>(item => item.LocationId == can.Id).ToArray()));
    }

    private static void PlaceRock(Fixture f, PilotCallContext context, double distance)
    {
        context.Flight.Checkpoint();
        var origin = context.Pilot.Position!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var rock = world.Set<WorldAsteroid>().Find(Fixture.Asteroid)!;
            (rock.X, rock.Y, rock.Z) = (origin.X + distance, origin.Y, origin.Z);
            world.SaveChanges();
        }
        context.Flight.RefreshAsteroids(f.Db.Services.GetRequiredService<IAsteroidField>().List(context.Pilot.SystemId));
    }

    private static AutoMiningStatus Run(AutoMiningHandler controller, PilotCallContext context, string method, params object?[] args) =>
        Assert.IsType<AutoMiningStatus>(
            Assert.IsType<PilotCallResult.Success>(controller.Call(context, AutoMiningHandler.Service, method, args)).Reply
        );
}
