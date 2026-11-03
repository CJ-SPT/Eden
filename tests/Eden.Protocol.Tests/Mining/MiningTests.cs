using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed class MiningTests
{
    private sealed class Clock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Milliseconds;

        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(Milliseconds);
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public Clock Clock { get; } = new();
        public SharedSpace Space { get; } = new();
        public SharedTargetLocks Locks { get; } = new();
        public IPilotMining Store => Db.Services.GetRequiredService<IPilotMining>();
        public IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();
        public const long Asteroid = AsteroidDefinitions.FirstAsteroidId;
        private readonly List<(PilotCallContext Context, TargetingHandler Targeting)> _clients = [];

        public Fixture()
        {
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO DogmaAttribute(Id,Name) VALUES (14,'hiSlots'),(48,'cpuOutput'),(11,'powerOutput'),(50,'cpu'),(30,'power'),
                    (182,'requiredSkill1'),(277,'requiredSkill1Level'),(6,'capacitorNeed'),(54,'maxRange'),(73,'duration'),
                        (77,'miningAmount'),
                    (76,'maxTargetRange'),(192,'maxLockedTargets'),(552,'signatureRadius'),(564,'scanResolution'),
                        (5967,'miningCritChance'),(5969,'miningCritBonusYield');
                INSERT INTO TypeAttribute VALUES (601,14,2),(601,48,135),(601,11,23),(601,76,27000),(601,192,3),(601,552,52),(601,564,475),
                    (3651,50,65),(3651,30,2),(3651,182,3386),(3651,277,1),(3651,6,5),(3651,54,10000),(3651,73,10000),(3651,77,6),
                        (3651,5967,0),(3651,5969,2);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (3386,'Mining',257),(3410,'Astrogeology',257),
                    (90727,'Mining Precision',257),(90728,'Mining Exploitation',257),
                    (3417,'Capacitor Systems Operation',257),(3418,'Capacitor Management',257);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (483,'Miner I',54),(482,'Miner II',54);
                INSERT INTO SourceRecord VALUES ('client/types','483','{{"volume":5}}'),('client/types','482','{{"volume":5}}');
                INSERT INTO TypeAttribute VALUES (483,50,60),(483,30,2),(483,182,3386),(483,277,1),(483,6,5),(483,54,10000),
                    (483,73,15000),(483,77,10),(483,5967,0),(483,5969,2),
                    (482,50,80),(482,30,4),(482,182,3386),(482,277,4),(482,6,18),(482,54,12000),(482,73,15000),(482,77,15),(482,5967,0),
                        (482,5969,2);
                INSERT INTO TypeAttribute VALUES (3386,275,1),(3410,275,3),(90727,275,2),(90728,275,6),(3417,275,1),(3418,275,3);
                INSERT INTO ItemCategory(Id,Name) VALUES (25,'Asteroid');
                INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (462,'Veldspar',25);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (1230,'Veldspar',462);
                INSERT INTO TypeAttribute VALUES (1230,552,100);
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types','1230','{{"volume":0.1}}');
                """
            );
            using WorldDbContext world = new(Db.Path);
            world.Database.ExecuteSqlRaw("INSERT INTO WorldPilotSkill VALUES (90000001,3386,250),(90000002,3386,250)");
            var pilot = Db.Store.FindPilot(1000001)!;
            world
                .Set<WorldAsteroid>()
                .Add(
                    new()
                    {
                        Id = Asteroid,
                        BeltId = 40009232,
                        SystemId = pilot.SystemId,
                        TypeId = 1230,
                        Quantity = 10000,
                        X = pilot.X + pilot.Flight.StationRadius + 1500,
                        Y = pilot.Y,
                        Z = pilot.Z,
                        Radius = 100,
                    }
                );
            world.SaveChanges();
        }

        public (PilotCallContext Context, TargetingHandler Targeting, MiningHandler Mining, long Module) Client(
            long account = 1000001,
            bool locked = true,
            MiningLaserType laser = MiningLaserType.Civilian
        )
        {
            var pilot = Db.Store.FindPilot(account)!;
            var fitting = Db.Services.GetRequiredService<IPilotFitting>();
            var item = Assert.Single(
                Inventory.List(pilot.PilotId, pilot.StationId),
                i => i.GroupId == InventoryDefinitions.MiningLaserGroup
            );
            if (laser != MiningLaserType.Civilian)
            {
                using WorldDbContext world = new(Db.Path);
                world.Set<OwnedItem>().Find(item.Id)!.TypeId = (int)laser;
                world.SaveChanges();
            }
            long module = fitting.Fit(pilot.PilotId, [item.Id], pilot.StationId, (int)InventoryFlag.AutoFit).Last().Item.Id;
            fitting.SetOnline(pilot.PilotId, pilot.ShipId, module, true);
            var objects = Db.Objects.Concat(Db.Services.GetRequiredService<IAsteroidField>().List(pilot.SystemId)).ToArray();
            PilotCallContext context = new(
                account,
                pilot.Account,
                [pilot],
                selected => new(Db.Store, selected, Clock, Space, objects, [new(module, (int)laser, (int)InventoryFlag.HighSlot0)]),
                account,
                []
            );
            context.Select(pilot);
            _ = context.Flight.CallWire("ship", "Undock", [pilot.ShipId]);
            context.Flight.Snapshot();
            TargetingHandler targeting = new(Clock, fitting, Locks);
            MiningHandler mining = new(Clock, Store, targeting);
            _clients.Add((context, targeting));
            if (locked)
            {
                _ = targeting.CallWire(context, "dogmaIM", "AddTarget", [Asteroid]);
                Clock.Milliseconds += 4000;
                targeting.Advance(context);
                Assert.True(targeting.IsLocked(Asteroid));
            }
            context.Notifications.Clear();
            context.Flight.Updates.Clear();
            return (context, targeting, mining, module);
        }

        public long Remaining()
        {
            using WorldDbContext world = new(Db.Path);
            return world.Set<WorldAsteroid>().Find(Asteroid)!.Quantity;
        }

        public void Disconnect(PilotCallContext context)
        {
            var client = _clients.Single(c => ReferenceEquals(c.Context, context));
            client.Targeting.Dispose();
            client.Context.Dispose();
            _clients.Remove(client);
        }

        public void Dispose()
        {
            foreach (var (context, targeting) in _clients)
            {
                targeting.Dispose();
                context.Dispose();
            }
            Db.Dispose();
        }
    }

    [Fact]
    public void VentureBonusesApplyAndMinedOreUsesItsHoldWhenCargoIsFull()
    {
        using Fixture fixture = new();
        PilotFleetTests.AddVenture(fixture.Db);
        var (context, targeting, mining, module) = fixture.Client();
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Ships.Find(context.Pilot.ShipId)!.TypeId = (int)StarterShipType.Venture;
            world.Add(
                new OwnedItem
                {
                    Id = module + 100,
                    OwnerId = context.Pilot.PilotId,
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    LocationId = context.Pilot.ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = 10,
                }
            );
            world.SaveChanges();
        }
        context.ChangeShip(fixture.Db.Store.FindPilot(context.AccountId)!);
        context.Flight.Snapshot();
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, MiningDefinitions.EffectName, Fixture.Asteroid, 0L]));
        fixture.Clock.Milliseconds += 10000;
        mining.Advance(context);
        var ore = Assert.Single(fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId), i => i.TypeId == 1230);
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, ore.FlagId);
        Assert.Equal(132, ore.Quantity);
        Assert.Equal(9868, fixture.Remaining());
        _ = new SqliteWorld(fixture.Db.Path);
        Assert.Equal(ore, fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Single(i => i.Id == ore.Id));
    }

    [Fact]
    public void MinerTwoRequiresMiningFourAtFittingAndActivation()
    {
        using Fixture fixture = new();
        Assert.Throws<InvalidDataException>(() => fixture.Client(laser: MiningLaserType.MinerII));
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Set<PilotSkill>().Find(90000001L, (int)MiningSkill.Mining)!.Points = TrainedSkill.PointsForLevel(1, 4);
            world.SaveChanges();
        }
        var (context, _, mining, module) = fixture.Client(laser: MiningLaserType.MinerII);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Set<PilotSkill>().Find(context.Pilot.PilotId, (int)MiningSkill.Mining)!.Points = TrainedSkill.PointsForLevel(1, 1);
            world.SaveChanges();
        }
        Assert.IsType<PilotCallRouter.Error>(
            mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 0L])
        );
        Assert.False(mining.IsActive(module));
        Assert.Equal(10000, fixture.Remaining());
        Assert.Equal(130, fixture.Store.Capacitor(context.Pilot.PilotId, fixture.Clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()));
    }

    [Theory]
    [InlineData(MiningLaserType.MinerI, 1, 105, 5)]
    [InlineData(MiningLaserType.MinerII, 4, 180, 18)]
    public void BetterLasersUseCatalogCyclesYieldAndCapacitor(MiningLaserType type, int level, int expectedOre, int capacitor)
    {
        using Fixture fixture = new();
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Set<PilotSkill>().Find(90000001L, (int)MiningSkill.Mining)!.Points = TrainedSkill.PointsForLevel(1, level);
            world.SaveChanges();
        }
        var (context, _, mining, module) = fixture.Client(laser: type);
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 0L]));
        Assert.Equal(
            130 - capacitor,
            fixture.Store.Capacitor(context.Pilot.PilotId, fixture.Clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
            8
        );
        Assert.Equal(15000d, mining.ActiveEffect(context.Pilot, module)!.DurationMilliseconds);
        fixture.Clock.Milliseconds += 14999;
        mining.Advance(context);
        Assert.DoesNotContain(fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId), i => i.TypeId == 1230);
        fixture.Clock.Milliseconds++;
        mining.Advance(context);
        var ore = Assert.Single(fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId), i => i.TypeId == 1230);
        Assert.Equal(expectedOre, ore.Quantity);
        Assert.Equal(10000 - expectedOre, fixture.Remaining());
        Assert.False(mining.IsActive(module));
        _ = new SqliteWorld(fixture.Db.Path);
        Assert.Equal(ore, fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Single(i => i.Id == ore.Id));
    }

    [Fact]
    public void NativeActivationWaitsForCyclePaysCapacitorAndRetriesDoNotDuplicateYield()
    {
        using Fixture fixture = new();
        var (context, targeting, mining, module) = fixture.Client();
        object? Activate() => mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 0L]);
        Assert.Null(Activate());
        Assert.True(mining.IsActive(module));
        var inventoryHandler = new InventoryHandler(
            fixture.Clock,
            fixture.Inventory,
            fixture.Db.Services.GetRequiredService<IPilotFitting>(),
            fixture.Store,
            mining,
            fixture.Db.Services.GetRequiredService<IPilotFleet>(),
            fixture.Db.Services.GetRequiredService<Eden.Protocol.Combat.IPilotHealth>(),
            fixture.Db.Services.GetRequiredService<Eden.Protocol.Combat.IPilotCombat>(),
            new Eden.Protocol.Combat.CombatHandler(
                fixture.Clock,
                fixture.Db.Services.GetRequiredService<Eden.Protocol.Combat.IPilotCombat>(),
                targeting
            ),
            fixture.Db.Services.GetRequiredService<Eden.Protocol.Loot.IWrecks>(),
            fixture.Db.Services.GetRequiredService<Eden.Protocol.Repair.IPilotShieldBoosting>(),
            new Eden.Protocol.Repair.ShieldBoostHandler(
                fixture.Clock,
                fixture.Db.Services.GetRequiredService<Eden.Protocol.Repair.IPilotShieldBoosting>()
            ),
            fixture.Db.Services.GetRequiredService<Eden.Protocol.Ammunition.IPilotAmmunition>(),
            new Eden.Protocol.Ammunition.AmmunitionHandler(
                fixture.Clock,
                fixture.Db.Services.GetRequiredService<Eden.Protocol.Ammunition.IPilotAmmunition>(),
                fixture.Db.Services.GetRequiredService<IPilotFitting>(),
                fixture.Db.Services.GetRequiredService<Eden.Protocol.Combat.IPilotCombat>(),
                fixture.Inventory
            ),
            fixture.Db.Services.GetRequiredService<Eden.Protocol.Skills.PilotBrain>(),
            fixture.Db.Services.GetRequiredService<Eden.Protocol.Propulsion.IPilotPropulsion>(),
            new(fixture.Clock, fixture.Db.Services.GetRequiredService<Eden.Protocol.Propulsion.IPilotPropulsion>())
        );
        var info = Assert.IsType<BlueInstance>(inventoryHandler.CallWire(context, "dogmaIM", "ItemGetInfo", [module]));
        var effects = Assert.IsType<Dictionary<long, object?>>(Assert.IsType<Dictionary<string, object?>>(info.State)["activeEffects"]);
        Assert.Equal(Fixture.Asteroid, Assert.IsType<object?[]>(effects[(long)DogmaEffect.MiningLaser])[3]);
        Assert.Equal(125, fixture.Store.Capacitor(context.Pilot.PilotId, fixture.Clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()), 8);
        Assert.Null(Activate());
        Assert.Equal(2, context.Notifications.Count);
        Assert.Equal(10000, fixture.Remaining());
        fixture.Clock.Milliseconds += 9999;
        mining.Advance(context);
        Assert.Single(fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId));
        fixture.Clock.Milliseconds++;
        mining.Advance(context);
        var ore = Assert.Single(fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId), i => i.TypeId == 1230);
        Assert.Equal(63, ore.Quantity); // Mining I: 6 * 1.05 mÂ³, 0.1 mÂ³ per unit.
        Assert.Equal(9937, fixture.Remaining());
        Assert.False(mining.IsActive(module));
        var insertion = Assert.IsType<Dictionary<long, object?>>(
            Assert.Single(context.Notifications, n => n.Name == "OnItemChange").Arguments[1]
        );
        Assert.Equal(0L, insertion[(long)InventoryColumn.Location]);
        Assert.Equal(0, insertion[(long)InventoryColumn.Flag]);
        mining.Advance(context);
        Assert.Equal(9937, fixture.Remaining());
        var effect = context.Notifications.First(n => n.Name == "OnGodmaShipEffect").Arguments;
        Assert.Equal(11, effect.Length); // Common signature accepted by godma and PlayerShip.
        Assert.Equal((int)DogmaEffect.MiningLaser, effect[1]);
        Assert.Equal(true, effect[3]);
        var env = Assert.IsType<object?[]>(effect[5]);
        Assert.Equal(Fixture.Asteroid, env[3]);
        var attribute = Assert.IsType<object?[]>(
            Assert.Single(Assert.IsType<List<object?>>(context.Notifications.First(n => n.Name == "OnModuleAttributeChanges").Arguments[0]))
        );
        Assert.Equal(8, attribute.Length); // clientDogmaLocation also unpacks wallclockTime.
        var message = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(context.Notifications, n => n.Name == "OnOreMined").Arguments[0]
        );
        Assert.Equal(63, message["quantity_added"]);
        Assert.NotEmpty(BlueMarshal.Encode(message));
        _ = new SqliteWorld(fixture.Db.Path);
        Assert.Equal(ore, fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Single(i => i.Id == ore.Id));
    }

    [Fact]
    public void ManualStopCollectsElapsedWholeUnitsAndAutoRepeatContinuesOnlyWhileActive()
    {
        using Fixture fixture = new();
        var (context, _, mining, module) = fixture.Client();
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 1000L]));
        fixture.Clock.Milliseconds += 10000;
        mining.Advance(context);
        Assert.True(mining.IsActive(module));
        fixture.Clock.Milliseconds += 5000;
        Assert.Null(mining.CallWire(context, "dogmaIM", "Deactivate", [module, "miningLaser"]));
        Assert.False(mining.IsActive(module));
        Assert.Equal(10000 - 63 - 31, fixture.Remaining());
        fixture.Clock.Milliseconds += 60000;
        mining.Advance(context);
        Assert.Equal(9906, fixture.Remaining());
        Assert.Null(mining.CallWire(context, "dogmaIM", "Deactivate", [module, "miningLaser"]));
        Assert.Equal(9906, fixture.Remaining());
    }

    [Fact]
    public void TargetLossWarpAndInvalidActivationDoNotProduceOre()
    {
        using Fixture fixture = new();
        var (context, targeting, mining, module) = fixture.Client(locked: false);
        Assert.IsType<PilotCallRouter.Error>(
            mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 1000L])
        );
        _ = targeting.CallWire(context, "dogmaIM", "AddTarget", [Fixture.Asteroid]);
        fixture.Clock.Milliseconds += 4000;
        targeting.Advance(context);
        Assert.IsType<PilotCallRouter.Error>(
            mining.CallWire(context, "dogmaIM", "Activate", [module + 100, "miningLaser", Fixture.Asteroid, 0L])
        );
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 1000L]));
        _ = targeting.CallWire(context, "dogmaIM", "RemoveTarget", [Fixture.Asteroid]);
        fixture.Clock.Milliseconds += 10000;
        mining.Advance(context);
        Assert.False(mining.IsActive(module));
        Assert.Equal(10000, fixture.Remaining());
        _ = targeting.CallWire(context, "dogmaIM", "AddTarget", [Fixture.Asteroid]);
        fixture.Clock.Milliseconds += 4000;
        targeting.Advance(context);
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 1000L]));
        _ = context.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", 50001249L]);
        targeting.Advance(context);
        mining.Advance(context);
        Assert.False(mining.IsActive(module));
        Assert.Equal(10000, fixture.Remaining());
    }

    [Fact]
    public void CargoLimitAndDepletionStopCyclesWithoutLosingOrDuplicatingOre()
    {
        using Fixture fixture = new();
        var (context, _, mining, module) = fixture.Client();
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world
                .Set<OwnedItem>()
                .Add(
                    new()
                    {
                        Id = InventoryDefinitions.FirstOwnedItemId + 100,
                        OwnerId = context.Pilot.PilotId,
                        LocationId = context.Pilot.ShipId,
                        FlagId = (int)InventoryFlag.Cargo,
                        TypeId = 1230,
                        Quantity = 1240,
                    }
                );
            world.SaveChanges();
        }
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 1000L]));
        fixture.Clock.Milliseconds += 10000;
        mining.Advance(context);
        Assert.False(mining.IsActive(module));
        Assert.Equal(9990, fixture.Remaining());
        Assert.Equal(1250, fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Single(i => i.TypeId == 1230).Quantity);
        Assert.IsType<PilotCallRouter.Error>(
            mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 0L])
        );
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DELETE FROM WorldItem WHERE TypeId=1230; UPDATE WorldAsteroid SET Quantity=7");
        }
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 1000L]));
        fixture.Clock.Milliseconds += 10000;
        mining.Advance(context);
        Assert.Equal(0, fixture.Remaining());
        Assert.False(mining.IsActive(module));
        Assert.Equal(7, fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Single(i => i.TypeId == 1230).Quantity);
        context.Flight.RefreshAsteroids(fixture.Db.Services.GetRequiredService<IAsteroidField>().List(context.Pilot.SystemId));
        Assert.Null(context.Flight.FindTarget(Fixture.Asteroid));
        _ = new SqliteWorld(fixture.Db.Path);
        Assert.Empty(new AsteroidFieldStore(new(fixture.Db.Path)).List(context.Pilot.SystemId));
    }

    [Fact]
    public async Task CompetingPilotsShareOneDepositAndCriticalBonusDoesNotConsumeIt()
    {
        using Fixture fixture = new();
        var (pilot, _, _, module) = fixture.Client();
        var (wing, _, _, wingModule) = fixture.Client(1000002);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldAsteroid SET Quantity=100");
        }
        var yields = await Task.WhenAll(
            Task.Run(() => fixture.Store.Collect(pilot.Pilot.PilotId, module, Fixture.Asteroid, 6.3, 2)),
            Task.Run(() => fixture.Store.Collect(wing.Pilot.PilotId, wingModule, Fixture.Asteroid, 6.3, 0))
        );
        Assert.Equal(100, yields.Sum(y => y.Extracted));
        Assert.Equal(yields[0].Extracted * 2, yields[0].CriticalBonus);
        Assert.Equal(0, fixture.Remaining());
        Assert.Equal(
            100 + yields.Sum(y => y.CriticalBonus),
            fixture
                .Inventory.List(pilot.Pilot.PilotId, pilot.Pilot.ShipId)
                .Concat(fixture.Inventory.List(wing.Pilot.PilotId, wing.Pilot.ShipId))
                .Where(i => i.TypeId == 1230)
                .Sum(i => i.Quantity)
        );
    }

    [Fact]
    public void MinedOreCanDockInAnotherSystemUnloadAndResumeFromTheNewStation()
    {
        using Fixture fixture = new();
        var (context, _, mining, module) = fixture.Client();
        Assert.Null(mining.CallWire(context, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 0L]));
        fixture.Clock.Milliseconds += 10000;
        mining.Advance(context);
        var ore = fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Single(i => i.TypeId == 1230);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                VALUES (60000100,52678,1000035,30000144,14,50002185,10001000000,20000000000,30000000000);
                """
            );
        }
        fixture.Disconnect(context);
        var objects = fixture.Db.Services.GetRequiredService<ISystemObjects>().Load(30000144);
        var station = objects.Single(o => o.Id == 60000100);
        var atStation = station.Position + new SpacePosition(station.Radius + 500, 0, 0);
        fixture.Db.Store.SetPosition(context.Pilot.ShipId, atStation, 30000144);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        Assert.Throws<InvalidDataException>(() => fixture.Db.Store.Dock(pilot.PilotId, pilot.ShipId + 1, 60000100));
        Assert.Throws<InvalidDataException>(() => fixture.Db.Store.Dock(pilot.PilotId, pilot.ShipId, pilot.StationId));
        using FlightSession flight = new(fixture.Db.Store, pilot, fixture.Clock, fixture.Space, objects, docking: fixture.Db.Store);
        flight.Snapshot();
        Assert.Null(flight.CallWire("beyonce", "CmdDock", [60000100L, pilot.ShipId]));
        Assert.Null(flight.Pilot.Position);
        Assert.Equal(60000100, flight.Pilot.StationId);
        Assert.Equal(30000144, flight.Pilot.SystemId);
        var unloaded = Assert
            .Single(fixture.Inventory.Move(pilot.PilotId, [ore.Id], pilot.ShipId, 60000100, (int)InventoryFlag.Hangar, null))
            .Item;
        Assert.Equal(63, unloaded.Quantity);
        Assert.Equal(ore.Id, unloaded.Id);
        _ = new SqliteWorld(fixture.Db.Path);
        Assert.Equal(flight.Pilot, fixture.Db.Store.FindPilot(1000001));
        Assert.Equal(unloaded, Assert.Single(fixture.Inventory.List(pilot.PilotId, 60000100)));
        Assert.Null(flight.CallWire("ship", "Undock", [pilot.ShipId]));
        Assert.Equal(30000144, fixture.Db.Store.FindPilot(1000001)!.SystemId);
        Assert.InRange(flight.Pilot.Position!.DistanceTo(station.Position), station.Radius + 499, station.Radius + 501);
    }

    [Fact]
    public void CriticalCycleAddsBonusOreAndPublishesLaserStateToTheOtherPilot()
    {
        using Fixture fixture = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1 WHERE ItemTypeId=3651 AND AttributeId=5967");
        }
        var (pilot, _, mining, module) = fixture.Client();
        var (wing, _, _, _) = fixture.Client(1000002);
        wing.Flight.RefreshPeers();
        wing.Flight.Updates.Clear();
        Assert.Null(mining.CallWire(pilot, "dogmaIM", "Activate", [module, "miningLaser", Fixture.Asteroid, 0L]));
        wing.Flight.RefreshPeers();
        var started = wing
            .Flight.Updates.Cast<object?[]>()
            .Select(u => Assert.IsType<object?[]>(u[1]))
            .Single(u => Equals(u[0], "OnSpecialFX"));
        var args = Assert.IsType<object?[]>(started[1]);
        Assert.Equal(pilot.Pilot.ShipId, args[0]);
        Assert.Equal(module, args[1]);
        Assert.Equal("effects.Laser", args[5]);
        Assert.Equal(true, args[7]);
        fixture.Clock.Milliseconds += 10000;
        mining.Advance(pilot);
        Assert.Equal(9937, fixture.Remaining());
        Assert.Equal(189, fixture.Inventory.List(pilot.Pilot.PilotId, pilot.Pilot.ShipId).Single(i => i.TypeId == 1230).Quantity);
        wing.Flight.Updates.Clear();
        wing.Flight.RefreshPeers();
        var stopped = wing
            .Flight.Updates.Cast<object?[]>()
            .Select(u => Assert.IsType<object?[]>(u[1]))
            .Single(u => Equals(u[0], "OnSpecialFX"));
        Assert.Equal(false, Assert.IsType<object?[]>(stopped[1])[7]);
    }

    [Fact]
    public void CapacitorCurveAndSavedChargeUseCatalogSkillsAndSurviveMigration()
    {
        Assert.Equal(25, ChargedResource.Recharged(25, 100, 10000, 0), 8);
        Assert.InRange(ChargedResource.Recharged(0, 100, 10000, 10000), 98, 100);
        using Fixture fixture = new();
        var (context, _, _, module) = fixture.Client();
        long now = fixture.Clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        for (int cycle = 0; cycle < 26; cycle++)
        {
            fixture.Store.Begin(context.Pilot.PilotId, module, Fixture.Asteroid, now);
        }
        Assert.Equal(0, fixture.Store.Capacitor(context.Pilot.PilotId, now), 8);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Begin(context.Pilot.PilotId, module, Fixture.Asteroid, now));
        _ = new SqliteWorld(fixture.Db.Path);
        Assert.Equal(0, fixture.Store.Capacitor(context.Pilot.PilotId, now), 8);
        Assert.InRange(fixture.Store.Capacitor(context.Pilot.PilotId, now + TimeSpan.TicksPerSecond * 65), 128, 130);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "INSERT INTO WorldPilotSkill VALUES "
                    + "(90000001,3417,8000),(90000001,3418,24000),(90000001,3410,4243),(90000001,90727,500),(90000001,90728"
                    + ",1500)"
            );
        }
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>().ShipAttributes(context.Pilot.PilotId);
        Assert.Equal(149.5, fitting[DogmaAttributeId.CapacitorCapacity], 8);
        Assert.Equal(55250, fitting[DogmaAttributeId.CapacitorRechargeTime], 8);
        var miner = fixture.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Single(i => i.Id == module);
        var attributes = fixture.Store.ModuleAttributes(context.Pilot.PilotId, miner);
        Assert.Equal(6 * 1.05 * 1.10, attributes[DogmaAttributeId.MiningAmount], 8);
        Assert.Equal(2.1, attributes[DogmaAttributeId.MiningCriticalBonusYield], 8);
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldShipEnergy; DROP TABLE WorldMarketTrade; DROP TABLE WorldMarketOrder; DROP TABLE "
                    + "WorldMarketStation; DROP TABLE WorldWallet; DROP TABLE WorldShipHealth; DROP TABLE WorldWreck; DROP "
                    + "TABLE WorldRepairPayment; DROP TABLE WorldNpcAggression; DROP TABLE WorldShipLoss; DROP TABLE "
                    + "WorldNpc; DROP TABLE WorldNpcBounty; DROP TABLE WorldWeaponCooldown; DELETE FROM WorldItem WHERE "
                    + "TypeId=3638; PRAGMA user_version=15"
            );
        }
        _ = new SqliteWorld(fixture.Db.Path);
        Assert.Equal(149.5, fixture.Store.Capacitor(context.Pilot.PilotId, now), 8);
        Assert.Equal(10000, fixture.Remaining());
    }
}
