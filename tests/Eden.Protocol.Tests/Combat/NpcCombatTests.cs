using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcCombatTests
{
    internal sealed class Fixture : IDisposable
    {
        public const long PilotId = 90000001;
        public const long ShipId = 1000000000001;
        public TestWorld Db { get; }
        public Clock Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public IShipLootRoll LootRoll { get; set; } = new FixedLootRoll(true);
        public INpcCombat Combat => new NpcCombatStore(new(Db.Path), Db.Store, LootRoll);
        public IPilotCombat Weapons => Db.Services.GetRequiredService<IPilotCombat>();
        public IPilotHealth Health => Db.Services.GetRequiredService<IPilotHealth>();
        public NpcContact Npc { get; }
        public long Module { get; }
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;

        public Fixture(bool liveWorld = false, bool simulationObjects = true, bool useTestClock = false)
        {
            Db = new(liveWorld: liveWorld, simulationObjects: simulationObjects, clock: useTestClock ? Clock : null);
            NpcEncounterTests.Prepare(Db);
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (6,'capacitorNeed'),(14,'hiSlots'),(48,'cpuOutput'),
                        (11,'powerOutput');
                    INSERT INTO TypeAttribute VALUES (601,14,2),(601,48,125),(601,11,23),
                        (3638,6,0),(3638,51,1000),(3638,54,10000),(3638,64,1),(3638,117,1),(3638,158,3000),(3638,160,500),(3638,620,40);
                    INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (29,'Capsule',6);
                    INSERT INTO SourceRecord VALUES ('client/types','670','{{"published":1,"volume":1000}}');
                    INSERT INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES (670,'Capsule',29,0,32000,2);
                    INSERT INTO TypeAttribute SELECT 670,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601;
                    INSERT OR REPLACE INTO TypeAttribute VALUES (670,9,150),(670,263,63),(670,265,156),(670,482,125),
                        (670,55,187500),(670,479,625000),(670,102,0),(670,14,0),(670,37,150),(670,70,2),(670,600,4),(670,552,25);
                    INSERT INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES
                        (26510,'Caldari Rookie ship Wreck',186,27500,10000,14);
                    INSERT INTO SourceRecord VALUES ('client/types','26510','{{"published":1,"marketGroupID":1,"volume":27500}}');
                    UPDATE SourceRecord SET Data='{{"published":1,"marketGroupID":1,"volume":15000,"wreckTypeID":26510}}'
                        WHERE Dataset='client/types' AND Key='601';
                    """
                );
            }
            Npc = Db.Services.GetRequiredService<INpcEncounters>().List(30000144, Clock.Now)[0];
            var inventory = Db.Services.GetRequiredService<IPilotInventory>();
            var fitting = Db.Services.GetRequiredService<IPilotFitting>();
            var gun = inventory.List(PilotId, Pilot.StationId).Single(i => i.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun);
            Module = Assert.Single(fitting.Fit(PilotId, [gun.Id], Pilot.StationId, (int)InventoryFlag.AutoFit)).Item.Id;
            fitting.SetOnline(PilotId, ShipId, Module, true);
            Db.Store.SetPosition(ShipId, Npc.Position + new SpacePosition(1000, 0, 0), Npc.SystemId);
        }

        public void Aggress() => Weapons.Fire(PilotId, Module, Npc.Id, SpacePosition.Zero, Clock.Now, 0.5);

        public NpcShot? Fire(double roll = 0.5) =>
            Combat.Fire(PilotId, ShipId, Npc.Id, SpacePosition.Zero, Clock.Now, roll, 1 + checked((int)(Clock.Milliseconds / 1000)));

        public void AlmostLost()
        {
            using WorldDbContext world = new(Db.Path);
            world.Add(
                new ShipHealthState
                {
                    ShipId = ShipId,
                    ShieldCharge = 0,
                    ArmorDamage = 125,
                    StructureDamage = 174,
                    UpdatedAt = Clock.Now,
                }
            );
            if (world.Set<PilotWallet>().Find(PilotId) is null)
            {
                world.Add(new PilotWallet { PilotId = PilotId, Cents = 12345 });
            }
            world.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    private sealed class FixedLootRoll(bool survives) : IShipLootRoll
    {
        public bool Survives() => survives;
    }

    private sealed class AlternatingLootRoll : IShipLootRoll
    {
        private int _rolls;

        public bool Survives() => _rolls++ % 2 == 0;
    }

    [Fact]
    public void ArrivalUsesCatalogDelayAndRestartRequiresFreshAggressionAndReaction()
    {
        using Fixture f = new();
        Assert.Empty(f.Combat.Aggressors(Fixture.ShipId));
        Assert.Null(f.Fire());
        f.Aggress();
        Assert.Equal(new[] { f.Npc.Id }, f.Combat.Aggressors(Fixture.ShipId));
        f.Clock.Milliseconds = 7499;
        Assert.Null(f.Fire());
        f.Clock.Milliseconds = 7500;
        var shot = Assert.IsType<NpcShot>(f.Fire());
        Assert.Equal(2.475, shot.Applied, 8);
        Assert.Equal(197.525, shot.Health.Remaining.Shield, 8);
        Assert.Equal(562, shot.Weapon.TurretTypeId);
        Assert.Equal(2500, shot.Weapon.Duration);
        Assert.Null(shot.Capsule);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Null(f.Fire());
        f.Clock.Milliseconds = 10000;
        Assert.Null(f.Fire());
        Assert.Empty(f.Combat.Aggressors(Fixture.ShipId));
        f.Aggress();
        f.Clock.Milliseconds = 17499;
        Assert.Null(f.Fire());
        f.Clock.Milliseconds = 17500;
        Assert.NotNull(f.Fire());
        var contact = f.Db.Services.GetRequiredService<INpcEncounters>().List(f.Npc.SystemId, f.Clock.Now).Single(n => n.Id == f.Npc.Id);
        Assert.Equal(Fixture.ShipId, contact.Weapon!.TargetId);
        Assert.Equal(f.Clock.Now, contact.Weapon.StartedAt);
    }

    [Fact]
    public async Task CompetingNpcTicksApplyOneShotAndDoNotCatchUpOfflineCycles()
    {
        using Fixture f = new();
        f.Aggress();
        f.Clock.Milliseconds = 60000;
        var shots = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => f.Fire())));
        Assert.Single(shots, s => s is not null);
        Assert.Equal(197.525, f.Health.Read(Fixture.PilotId, f.Clock.Now).Remaining.Shield, 8);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + 2500 * TimeSpan.TicksPerMillisecond, world.Set<NpcAggression>().Single().ReadyAt);
    }

    [Fact]
    public void IntegratedNpcWeaponDoesDamageWithoutInventingATurretGraphic()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=30380 AND AttributeId=245");
        }
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        var shot = Assert.IsType<NpcShot>(f.Fire());
        Assert.Equal(2.475, shot.Applied, 8);
        Assert.Equal(0, shot.Weapon.TurretTypeId);
        var message = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(
                Assert.IsType<object?[]>(
                    BlueMarshal.Decode(BlueMarshal.Encode(new DamageMessageNotification(CombatMessages.Incoming(shot)).Arguments))
                )
            )
        );
        Assert.DoesNotContain("weapon", message.Keys);
        Assert.Equal("otherPlayerWeapons", BlueMarshal.Text(message["attackType"]));
        Assert.Equal(shot.Applied, message["damage"]);
        Assert.Equal(shot.NpcId, message["attackerID"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingTheBeltLeashOrDockingEndsTheFight(bool dock)
    {
        using Fixture f = new();
        f.Aggress();
        f.Db.Store.SetPosition(
            Fixture.ShipId,
            dock ? null : f.Npc.Position + new SpacePosition(NpcEncounterDefinitions.ChaseLeash + 1, 0, 0),
            f.Npc.SystemId
        );
        Assert.Null(f.Fire());
        Assert.Empty(f.Combat.Aggressors(Fixture.ShipId));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<ShipHealthState>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalDamageAtomicallyRecordsLossCreatesOneCapsuleAndRecoverableWreck(bool resident)
    {
        using Fixture f = new(resident);
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        f.AlmostLost();
        using (WorldDbContext before = new(f.Db.Path))
        {
            before.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(before),
                    OwnerId = Fixture.PilotId,
                    TypeId = 34,
                    LocationId = Fixture.ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = 123,
                }
            );
            before.SaveChanges();
        }
        var shots = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => f.Fire())));
        var fatal = Assert.Single(shots, s => s is not null)!;
        Assert.True(fatal.Health.Remaining.Destroyed);
        var capsule = Assert.IsType<DockedPilot>(fatal.Capsule);
        Assert.Equal(FleetDefinitions.CapsuleType, capsule.ShipTypeId);
        Assert.Equal(f.Npc.Position + new SpacePosition(1000, 0, 0), capsule.Position);
        Assert.Equal(f.Npc.SystemId, capsule.SystemId);
        Assert.True(f.Health.WasDestroyed(Fixture.ShipId));
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Null(saved.Ships.Find(Fixture.ShipId));
            Assert.Empty(saved.Set<OwnedItem>().Where(i => i.LocationId == Fixture.ShipId));
            Assert.Empty(saved.Set<ModuleCooldown>());
            Assert.Empty(saved.Set<ShipHealthState>());
            Assert.Empty(saved.Set<NpcAggression>());
            Assert.Equal(12345, saved.Set<PilotWallet>().Single().Cents);
            var loss = Assert.Single(saved.Set<ShipLoss>());
            Assert.Equal(capsule.ShipId, loss.CapsuleId);
            Assert.Equal(capsule.Position, new SpacePosition(loss.X, loss.Y, loss.Z));
            var items = JsonSerializer.Deserialize<OwnedItem[]>(loss.ItemsJson)!;
            Assert.Contains(items, i => i.Id == f.Module && i.Singleton && i.Online);
            Assert.Contains(items, i => i.TypeId == 34 && i.Quantity == 123);
            var wreck = Assert.Single(saved.Set<WorldWreck>());
            Assert.Equal(Fixture.ShipId, wreck.ShipId);
            Assert.Null(wreck.NpcId);
            Assert.Equal(26510, wreck.TypeId);
            Assert.Equal(Fixture.PilotId, wreck.OwnerId);
            var contents = saved.Set<OwnedItem>().Where(i => i.LocationId == wreck.Id).OrderBy(i => i.Id).ToArray();
            Assert.Equal(items.Select(i => i.Id), contents.Select(i => i.Id));
            Assert.Equal(items.Select(i => i.Id), JsonSerializer.Deserialize<long[]>(loss.DroppedItemIdsJson));
            Assert.All(
                contents,
                i =>
                {
                    Assert.False(i.Online);
                    Assert.Equal((int)InventoryFlag.Cargo, i.FlagId);
                }
            );
        }
        f.Db.Services.GetService<LiveWorld>()?.Dispose();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(capsule.ShipId, f.Pilot.ShipId);
        f.Db.Store.SetPosition(capsule.ShipId, null);
        var recovery = f.Db.Services.GetRequiredService<IPilotFleet>().RecoverCorvette(Fixture.PilotId, capsule.ShipId, capsule.StationId);
        Assert.Equal(FleetDefinitions.CorvetteGroup, recovery.Pilot.ShipGroupId);
        Assert.Equal(4, recovery.Changes.Count);
        Assert.Equal(0, Assert.Single(recovery.Changes, c => c.Item.Id == capsule.ShipId).Item.Quantity);
        Assert.Null(f.Db.Services.GetRequiredService<IPilotFleet>().Find(Fixture.PilotId, capsule.ShipId));
        Assert.NotEqual(Fixture.ShipId, recovery.Pilot.ShipId);
        // Return in the recovery hull and recover the original singleton, not a
        // new module grant. A restart never rolls the loss contents again.
        f.Db.Store.SetPosition(recovery.Pilot.ShipId, capsule.Position, capsule.SystemId);
        var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
        var contact = Assert.Single(wrecks.List(capsule.SystemId, f.Clock.Now));
        var loot = wrecks.Read(Fixture.PilotId, contact.Id, f.Clock.Now).Items;
        wrecks.Take(Fixture.PilotId, recovery.Pilot.ShipId, contact.Id, loot.Select(i => i.Id).ToArray(), null, f.Clock.Now);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext restored = new(f.Db.Path);
        var module = restored.Set<OwnedItem>().Find(f.Module)!;
        Assert.Equal(recovery.Pilot.ShipId, module.LocationId);
        Assert.True(module.Singleton);
        Assert.False(module.Online);
        Assert.True(Assert.Single(wrecks.List(capsule.SystemId, f.Clock.Now)).Empty);
    }

    [Fact]
    public void FailedCapsuleCreationRollsBackDamageCooldownLossAndInventory()
    {
        using Fixture f = new();
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        f.AlmostLost();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE ItemType SET GroupId=237 WHERE Id=670");
        }
        Assert.Throws<InvalidDataException>(() => f.Fire());
        Assert.Equal(Fixture.ShipId, f.Pilot.ShipId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ShipLoss>());
        Assert.Equal(174, saved.Set<ShipHealthState>().Single().StructureDamage);
        Assert.NotNull(saved.Set<OwnedItem>().Find(f.Module));
        Assert.Null(saved.Set<NpcAggression>().Single().LastShotAt);
        Assert.Equal(12345, saved.Set<PilotWallet>().Single().Cents);
    }

    [Fact]
    public void MixedDropsPreserveOriginalManifestAndMoveOnlySurvivingItemsOffline()
    {
        using Fixture f = new();
        f.LootRoll = new AlternatingLootRoll();
        f.Aggress();
        f.AlmostLost();
        f.Clock.Milliseconds = 7500;
        long cargo;
        long mining;
        using (WorldDbContext world = new(f.Db.Path))
        {
            cargo = InventoryPersistence.NextId(world);
            mining = cargo + 1;
            world.AddRange(
                new OwnedItem
                {
                    Id = cargo,
                    OwnerId = Fixture.PilotId,
                    TypeId = 34,
                    LocationId = Fixture.ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = 23,
                },
                new OwnedItem
                {
                    Id = mining,
                    OwnerId = Fixture.PilotId,
                    TypeId = 34,
                    LocationId = Fixture.ShipId,
                    FlagId = (int)InventoryFlag.GeneralMiningHold,
                    Quantity = 37,
                }
            );
            world.SaveChanges();
        }
        Assert.NotNull(f.Fire()!.Capsule);
        using WorldDbContext saved = new(f.Db.Path);
        var loss = Assert.Single(saved.Set<ShipLoss>());
        var original = JsonSerializer.Deserialize<OwnedItem[]>(loss.ItemsJson)!;
        Assert.Equal(3, original.Length);
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, original.Single(i => i.Id == mining).FlagId);
        Assert.True(original.Single(i => i.Id == f.Module).Online);
        Assert.Equal(new[] { f.Module, mining }, JsonSerializer.Deserialize<long[]>(loss.DroppedItemIdsJson));
        var wreck = Assert.Single(saved.Set<WorldWreck>());
        Assert.Null(saved.Set<OwnedItem>().Find(cargo));
        var gun = saved.Set<OwnedItem>().Find(f.Module)!;
        Assert.Equal(wreck.Id, gun.LocationId);
        Assert.True(gun.Singleton);
        Assert.False(gun.Online);
        Assert.Equal(37, saved.Set<OwnedItem>().Find(mining)!.Quantity);
        Assert.Empty(saved.Set<ModuleCooldown>());
        var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
        var contents = wrecks.Read(Fixture.PilotId, wreck.Id, f.Clock.Now).Items;
        Assert.Equal(2, contents.Count);
        Assert.Throws<CargoCapacityException>(() => wrecks.Take(Fixture.PilotId, f.Pilot.ShipId, wreck.Id, [f.Module], null, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => wrecks.Read(90000002, wreck.Id, f.Clock.Now));
    }

    [Fact]
    public void NoSurvivorsStillLeavesAnEmptyCatalogWreckAndNeverRerollsOnRestart()
    {
        using Fixture f = new();
        f.LootRoll = new FixedLootRoll(false);
        f.Aggress();
        f.AlmostLost();
        f.Clock.Milliseconds = 7500;
        Assert.NotNull(f.Fire()!.Capsule);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Null(f.Fire());
        var contact = Assert.Single(f.Db.Services.GetRequiredService<IWrecks>().List(f.Npc.SystemId, f.Clock.Now));
        Assert.True(contact.Empty);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<OwnedItem>().Find(f.Module));
        Assert.Equal("[]", saved.Set<ShipLoss>().Single().DroppedItemIdsJson);
        Assert.Single(saved.Set<ShipLoss>());
    }

    [Fact]
    public void MissingPlayerWreckDefinitionRollsBackTheEntireFatalShot()
    {
        using Fixture f = new();
        f.Aggress();
        f.AlmostLost();
        f.Clock.Milliseconds = 7500;
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data='{{}}' WHERE Dataset='client/types' AND Key='601'");
        }
        Assert.Throws<InvalidDataException>(() => f.Fire());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(Fixture.ShipId, f.Pilot.ShipId);
        Assert.Empty(saved.Set<WorldWreck>());
        Assert.Empty(saved.Set<ShipLoss>());
        Assert.Equal(174, saved.Set<ShipHealthState>().Single().StructureDamage);
        Assert.True(saved.Set<OwnedItem>().Find(f.Module)!.Online);
        Assert.Null(saved.Set<NpcAggression>().Single().LastShotAt);
    }

    [Fact]
    public void VersionTwentyUpgradePreservesShipsItemsHealthResourcesAndWalletWithoutStartingFights()
    {
        using Fixture f = new();
        f.Aggress();
        f.AlmostLost();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                TestWorld.DropTestingMarketSchema
                    + TestWorld.DropNpcNavigationColumns
                    + "DROP TABLE WorldWreck; DROP TABLE WorldRepairPayment; DROP TABLE WorldNpcAggression; DROP TABLE "
                    + "WorldShipLoss; ALTER TABLE WorldWeaponCooldown DROP COLUMN Kind; PRAGMA user_version=20"
            );
        }
        _ = WorldInitialization.Open(f.Db.Path);
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(Fixture.ShipId, f.Pilot.ShipId);
        Assert.Equal(100012345, saved.Set<PilotWallet>().Find(90000001L)!.Cents);
        Assert.Equal(174, saved.Set<ShipHealthState>().Single().StructureDamage);
        Assert.Equal(130, saved.Set<ShipEnergy>().Single().Charge);
        Assert.NotNull(saved.Set<OwnedItem>().Find(f.Module));
        Assert.Single(saved.Set<ModuleCooldown>());
        Assert.Equal(19.01, saved.Set<WorldNpc>().Single(n => n.Id == f.Npc.Id).ShieldCharge, 8);
        Assert.Empty(saved.Set<NpcAggression>());
        Assert.Empty(saved.Set<ShipLoss>());
    }

    [Fact]
    public void ASecondAttackerDoesNotStealTheNpcsCurrentTarget()
    {
        using Fixture f = new();
        f.Aggress();
        var wing = f.Db.Store.FindPilot(1000002)!;
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        var fitting = f.Db.Services.GetRequiredService<IPilotFitting>();
        var gun = inventory.List(wing.PilotId, wing.StationId).Single(i => i.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun);
        long module = Assert.Single(fitting.Fit(wing.PilotId, [gun.Id], wing.StationId, (int)InventoryFlag.AutoFit)).Item.Id;
        fitting.SetOnline(wing.PilotId, wing.ShipId, module, true);
        f.Db.Store.SetPosition(wing.ShipId, f.Pilot.Position, f.Npc.SystemId);
        f.Weapons.Fire(wing.PilotId, module, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
        Assert.Empty(f.Combat.Aggressors(wing.ShipId));
        f.Clock.Milliseconds = 7500;
        Assert.Null(f.Combat.Fire(wing.PilotId, wing.ShipId, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5, 1));
        Assert.NotNull(f.Fire());
        Assert.Equal(200, f.Health.Read(wing.PilotId, f.Clock.Now).Remaining.Shield);
    }

    [Fact]
    public void CorvetteRoleResistanceMultipliesShieldResonancesBeforeIncomingDamage()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO DogmaAttribute(Id,Name) VALUES (1829,'rookieShieldResistBonus'),(273,'shieldKineticDamageResonance'),
                    (274,'shieldThermalDamageResonance');
                INSERT INTO TypeAttribute VALUES (601,1829,-8),(601,273,0.6),(601,274,0.8);
                """
            );
        }
        f.Aggress();
        f.Clock.Milliseconds = 7500;
        var shot = Assert.IsType<NpcShot>(f.Fire());
        Assert.Equal(0.552, shot.Health.Defense.ShieldResonance.Kinetic, 8);
        Assert.Equal(1.5939, shot.Applied, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FatalRetaliationStopsTheGunLoadsACapsuleAndRemovesTheOldHullForObservers(bool insured)
    {
        using Fixture f = new();
        if (insured)
        {
            InsuranceTestData.Prepare(f.Db);
            InsuranceTestData.Fund(f.Db);
            var position = f.Pilot.Position!;
            f.Db.Store.SetPosition(Fixture.ShipId, null);
            new PilotInsuranceStore(new(f.Db.Path), f.Clock).Insure(Fixture.PilotId, f.Pilot.StationId, Fixture.ShipId, 30000, false);
            f.Db.Store.SetPosition(Fixture.ShipId, position, f.Npc.SystemId);
        }
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=10000 WHERE ItemTypeId=30380 AND AttributeId=54");
        }
        var fitting = f.Db.Services.GetRequiredService<IPilotFitting>();
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        SharedSpace space = new();
        SharedTargetLocks locks = new();
        using TargetingHandler targeting = new(f.Clock, fitting, locks);
        MiningHandler mining = new(f.Clock, f.Db.Services.GetRequiredService<IPilotMining>(), targeting);
        CombatHandler weapons = new(f.Clock, f.Weapons, targeting);
        using NpcCombatHandler npcCombat = new(
            f.Clock,
            f.Combat,
            locks,
            new ShipLossCoordinator(
                mining,
                weapons,
                new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Repair.IPilotShieldBoosting>()),
                new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotPropulsion>()),
                new Eden.Protocol.Docking.DeferredRefitPrimes(
                    new Eden.Protocol.Ammunition.MagazineWire(
                        f.Clock,
                        fitting,
                        f.Weapons,
                        f.Db.Services.GetRequiredService<IPilotInventory>()
                    ),
                    f.Db.Services.GetRequiredService<Eden.Gameplay.Ammunition.IPilotAmmunition>()
                ),
                new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Repair.IPilotLocalRepairing>()),
                new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Repair.IPilotHardening>()),
                new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Ships.IPilotCapacitorBoosting>())
            ),
            space
        );
        using PilotCallContext context = new(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            p =>
                new(
                    f.Db.Store,
                    p,
                    f.Clock,
                    space,
                    [],
                    health: f.Health,
                    encounters: encounters,
                    wrecks: f.Db.Services.GetRequiredService<IWrecks>()
                ),
            42,
            SignedSettings.Empty
        );
        context.Select(f.Pilot);
        context.Flight.Snapshot();
        npcCombat.Enter(context);
        Assert.IsType<object?[]>(targeting.CallWire(context, "dogmaIM", "AddTarget", [f.Npc.Id]));
        f.Clock.Milliseconds = 5000;
        targeting.Advance(context);
        Assert.Null(weapons.CallWire(context, "dogmaIM", "Activate", [f.Module, "targetAttack", f.Npc.Id, 100L]));
        npcCombat.Advance(context);
        targeting.Advance(context);
        Assert.Contains(context.Notifications, n => n.Name == "OnTarget" && Equals(n.Arguments[0], "otheradd"));
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, f.Pilot.Position! + new SpacePosition(100, 0, 0), f.Npc.SystemId);
        using FlightSession observer = new(
            f.Db.Store,
            f.Db.Store.FindPilot(1000002)!,
            f.Clock,
            space,
            [],
            health: f.Health,
            encounters: encounters,
            wrecks: f.Db.Services.GetRequiredService<IWrecks>()
        );
        observer.Snapshot();
        context.Notifications.Clear();
        context.NotificationsBeforeFlight.Clear();
        context.Flight.Updates.Clear();
        observer.Updates.Clear();
        f.Clock.Milliseconds = 12500;
        f.AlmostLost();
        npcCombat.Advance(context);
        targeting.Advance(context);
        observer.RefreshWrecks();
        observer.RefreshPeers();
        Assert.Equal(FleetDefinitions.CapsuleType, context.Pilot.ShipTypeId);
        if (insured)
        {
            Assert.Equal(10700d, Assert.Single(context.Notifications.OfType<AccountBalanceChanged>()).Balance);
            using var walletScope = f.Db.Services.CreateScope();
            WalletHandler wallet = ActivatorUtilities.CreateInstance<WalletHandler>(walletScope.ServiceProvider, f.Clock);
            var journal = Assert.IsType<BlueList<WalletJournalEntry>>(
                Assert.IsType<PilotCallResult.Success>(wallet.Call(context, "account", "GetTransactions", [1000L, null, null, false])).Reply
            );
            var payout = Assert.Single(journal.Items, entry => entry.Type == WalletEntryType.Insurance && entry.AmountCents > 0);
            Assert.Equal(100000, payout.AmountCents);
            Assert.Equal(1070000, payout.BalanceCents);
            Assert.Equal(1000113, payout.OwnerId1);
            Assert.Equal(Fixture.PilotId, payout.OwnerId2);
            Assert.Equal(601, payout.ReferenceId);
            Assert.NotEmpty(BlueMarshal.Encode(payout));
        }
        else
        {
            Assert.Empty(context.Notifications.OfType<AccountBalanceChanged>());
        }
        Assert.Contains(
            context.Notifications,
            n => n.Name == "OnGodmaShipEffect" && Equals(n.Arguments[0], f.Module) && Equals(n.Arguments[3], false)
        );
        Assert.False(locks.Contains(f.Npc.Id, Fixture.ShipId));
        Assert.Contains(
            context.Notifications,
            n => n.Name == "OnTarget" && Equals(n.Arguments[0], "lost") && Equals(n.Arguments[1], f.Npc.Id)
        );
        Assert.Contains(
            context.Notifications,
            n => n.Name == "OnTarget" && Equals(n.Arguments[0], "otherlost") && Equals(n.Arguments[1], f.Npc.Id)
        );
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.Single(context.NotificationsBeforeFlight).Arguments));
        Assert.Equal("otherPlayer", message["attackType"]);
        Assert.Equal(f.Npc.Id, message["source"]);
        Assert.Equal(f.Npc.Id, message["attackerID"]);
        Assert.Equal(new object?[] { (int)MessageArgumentType.TypeId, f.Npc.TypeId }, message["owner"]);
        Assert.NotEmpty(BlueMarshal.Encode(message));
        var ownActions = context.Flight.WireUpdates().Cast<object?[]>().Select(u => (object?[])u[1]!).ToList();
        int added = ownActions.FindIndex(a => Equals(a[0], "AddBalls"));
        int removed = ownActions.FindIndex(a => Equals(a[0], "RemoveBall"));
        Assert.True(added >= 0 && removed > added);
        Assert.Equal(2, ownActions.Take(removed).Count(a => Equals(a[0], "AddBalls")));
        var ownWreck = Assert.Single(context.Flight.VisibleWrecks);
        Assert.Equal(Fixture.ShipId, ownWreck.SourceId);
        var wreckPayload = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<object?[]>(ownActions[added][1])));
        var slim = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(wreckPayload[1]))).State
        );
        Assert.Equal(Fixture.ShipId, slim["launcherID"]);
        Assert.Equal(new object?[] { Fixture.ShipId, true }, ownActions[removed][1]);
        Assert.DoesNotContain(ownActions, a => Equals(a[0], "SetState"));
        var peerActions = observer.WireUpdates().Cast<object?[]>().Select(u => (object?[])u[1]!).ToArray();
        Assert.Contains(
            peerActions,
            a => Equals(a[0], "RemoveBall") && Equals(((object?[])a[1]!)[0], Fixture.ShipId) && Equals(((object?[])a[1]!)[1], true)
        );
        Assert.Contains(peerActions, a => Equals(a[0], "AddBalls"));
        Assert.Equal("AddBalls", peerActions[0][0]);
        Assert.Single(observer.VisibleWrecks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldCargoRefreshDuringEjectionReadsAnEmptyInventoryAndNeverRestoresAssets(bool depart)
    {
        using Fixture f = new();
        var old = f.Pilot;
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            old.Account,
            [old],
            p => new(f.Db.Store, p, f.Clock, new(), []),
            7,
            SignedSettings.Empty
        );
        context.Select(old);
        object? Call(string service, string method, object?[] args) => router.CallWire(context, service, method, args, _ => { });
        var previous = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", [old.ShipId, 0L]));
        f.Aggress();
        f.AlmostLost();
        f.Clock.Milliseconds = 7500;
        var capsule = f.Fire()!.Capsule!;
        context.ReplaceDestroyedShip(capsule);
        // The pinned holdTracker rebinds via this nested MachoBindObject call
        // after session.shipid has changed but before its old controller retires.
        var bound = Assert.IsType<object?[]>(
            Call(
                "invbroker",
                "MachoBindObject",
                [
                    new object?[] { (long)capsule.SystemId, 5L },
                    new object?[] { "GetInventoryFromId", new object?[] { old.ShipId, 0L }, new Dictionary<string, object?>() },
                ]
            )
        );
        var inventory = Assert.IsType<BlueObjectReference>(bound[1]);
        Assert.Equal(previous, inventory);
        string service = context.ResolveReference(inventory.Id)!;
        Assert.Empty(Assert.IsType<List<object?>>(Call(service, "List", [(long)InventoryFlag.Cargo])));
        Assert.Empty(Assert.IsType<List<object?>>(Call(service, "ListByFlags", [])));
        var row = Assert.IsType<object?[]>(Assert.IsType<BlueConstructor>(Call(service, "GetSelfInvItem", [])).Arguments[1]);
        Assert.Equal(old.ShipId, row[0]);
        Assert.Equal((long)InventoryLocation.Graveyard, row[(int)InventoryColumn.Location]);
        Assert.Equal(InventoryWireDefinitions.AssembledQuantity, row[(int)InventoryColumn.Quantity]);
        Assert.IsType<PilotCallResult.Failure>(Call(service, "Add", [f.Module, capsule.ShipId]));
        Assert.IsType<PilotCallResult.Failure>(Call(service, "MultiAdd", [new List<object?> { f.Module }, capsule.ShipId]));
        Assert.Same(PilotCallResult.Unsupported, Call("invbroker", "GetInventoryFromId", [1000000000002L, 0L]));
        if (depart)
        {
            context.ReleaseLocationReferences();
        }
        else
        {
            context.ChangeShip(capsule);
        }
        Assert.Null(context.DestroyedShip);
        Assert.IsType<PilotCallResult.Failure>(Call(service, "List", []));
        Assert.Same(PilotCallResult.Unsupported, Call("invbroker", "GetInventoryFromId", [old.ShipId, 0L]));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Ships.Find(old.ShipId));
        Assert.Empty(saved.Set<OwnedItem>().Where(i => i.LocationId == old.ShipId));
        Assert.Single(saved.Set<ShipLoss>());
        Assert.Single(saved.Set<WorldWreck>());
        Assert.Equal(capsule.ShipId, saved.Pilots.Find(old.PilotId)!.ActiveShipId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SessionSendsTheFatalMessageAndCapsuleBallBeforeChangingShipId(bool rejectedCall, bool resident)
    {
        using Fixture f = new(resident, useTestClock: true);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                UPDATE TypeAttribute SET Value=10000 WHERE ItemTypeId=30380 AND AttributeId=54;
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES
                    ('client/asteroidcountbytypeidbeltid','40009232','{{}}'),('client/asteroidcountbytypeidbeltid','40009245','{{}}');
                """
            );
        }
        const string address = "carbon.common.script.net.machoNetAddress.MachoAddress";
        BlueInstance Request(string service, string method, params object?[] args) =>
            new(
                "carbon.common.script.net.machoNetPacket.CallReq",
                new object?[]
                {
                    6L,
                    new BlueInstance(address, new object?[] { 2L, 0L, 1L, null }),
                    new BlueInstance(address, new object?[] { 8L, service, null }),
                    null,
                    new object?[]
                    {
                        new object?[] { 0L, BlueMarshal.Encode(new object?[] { null, method, args, new Dictionary<string, object?>() }) },
                    },
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                }
            );
        Queue<BlueInstance> input = new([
            Request("charUnboundMgr", "SelectCharacterID", Fixture.PilotId),
            Request("beyonce", "MachoBindObject", (long)f.Npc.SystemId, null),
            Request("config", rejectedCall ? "UnimplementedAcceptanceCall" : "GetMultiLocationsEx", new List<object?> { f.Npc.Id }),
        ]);
        List<BlueInstance> packets = [];
        using var scope = f.Db.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () =>
                {
                    if (input.Count == 1)
                    {
                        f.Clock.Seconds += 8;
                        using WorldDbContext world = new(f.Db.Path);
                        world.Add(new NpcAggression { NpcId = f.Npc.Id, TargetShipId = Fixture.ShipId });
                        world.Add(
                            new ShipHealthState
                            {
                                ShipId = Fixture.ShipId,
                                ShieldCharge = 0,
                                ArmorDamage = 125,
                                StructureDamage = 174,
                                UpdatedAt = f.Clock.Now,
                            }
                        );
                        world.SaveChanges();
                    }
                    return input.TryDequeue(out var request) ? Task.FromResult<object?>(request) : throw new EndOfStreamException();
                },
                packet =>
                {
                    packets.Add(Assert.IsType<BlueInstance>(packet));
                    return Task.CompletedTask;
                },
                _ => { },
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        int transition = packets.FindLastIndex(p => p.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal));
        int damage = -1;
        int removal = -1;
        for (int index = 0; index < transition; index++)
        {
            var packet = packets[index];
            if (!packet.Name.EndsWith(".Notification", StringComparison.Ordinal))
            {
                continue;
            }
            var state = BlueRead.Tuple(packet.State, 14);
            var destination = Assert.IsType<BlueInstance>(state[2]);
            string name = BlueMarshal.Text(BlueRead.Tuple(destination.State, 4)[1]);
            if (name == "OnDamageMessage")
            {
                damage = index;
            }
            if (name != "DoDestinyUpdate")
            {
                continue;
            }
            var payload = BlueRead.Tuple(Assert.Single(BlueRead.Tuple(state[4], 1)), 2);
            var call = BlueRead.Tuple(BlueMarshal.Decode(BlueRead.Bytes(payload[1])), 2);
            var args = Assert.IsType<object?[]>(BlueRead.Tuple(call[1], 2)[1]);
            var actions = Assert.IsType<List<object?>>(args[0]).Cast<object?[]>().Select(u => (object?[])u[1]!).ToList();
            int removed = actions.FindIndex(a => BlueMarshal.Text(a[0]) == "RemoveBall" && Equals(((object?[])a[1]!)[0], Fixture.ShipId));
            if (removed < 0)
            {
                continue;
            }
            Assert.True(actions.FindIndex(a => BlueMarshal.Text(a[0]) == "AddBalls") < removed);
            Assert.Equal(true, ((object?[])actions[removed][1]!)[1]);
            removal = index;
        }
        Assert.True(damage >= 0 && removal > damage && transition > removal);
        int reply = packets.FindLastIndex(p => p.Name.EndsWith(rejectedCall ? ".ErrorResponse" : ".CallRsp", StringComparison.Ordinal));
        Assert.True(reply > transition, "The final call reply must follow the capsule transition.");
        Assert.Equal(FleetDefinitions.CapsuleType, f.Pilot.ShipTypeId);
    }
}
