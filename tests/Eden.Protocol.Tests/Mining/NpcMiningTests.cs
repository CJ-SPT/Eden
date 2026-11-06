using System.Text.Json;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Propulsion;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CombatFixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class NpcMiningTests
{
    private const int SystemId = 30000142;
    private const long PilotId = 90000001;
    private const long ShipId = 1000000000001;

    private static TestClock Clock() => new(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(250));

    private static void Prepare(TestWorld db, NpcEmpireFaction faction = NpcEmpireFaction.Caldari, double security = 0.949)
    {
        RegionalNpcTests.Prepare(db, security);
        string path = new WorldDatabase(db.Path).CatalogPath;
        PassiveCatalogFixture.LoadNpcMining(path);
        using CatalogDbContext catalog = new(path, writable: true);
        string json = JsonSerializer.Serialize(new { securityStatus = security, factionID = (int)faction });
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{json})");
        foreach (int belt in catalog.Set<Celestial>().Where(c => c.SolarSystemId == SystemId && c.GroupId == 9).Select(c => c.Id))
        {
            string key = belt.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            string counts = """{"1230":1}""";
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid',{key},{counts})"
            );
        }
    }

    private static NpcContact[] Read(TestWorld db, long now) =>
        db.Services.GetRequiredService<INpcEncounters>().List(SystemId, now).Where(n => NpcPopulationRules.IsMining(n.Role)).ToArray();

    private static void SeedDirect(TestWorld db, long now)
    {
        using WorldDbContext world = new(db.Path);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        db.Services.GetRequiredService<NpcPopulationSeeder>().Seed(world, catalog, SystemId, now);
        world.SaveChanges();
        transaction.Commit();
    }

    private static long MiningDue(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return world.Set<WorldNpcMiningMember>().Where(m => m.MiningDueAt != null).Min(m => m.MiningDueAt)!.Value;
    }

    private static long PickupAt(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return world.Set<WorldNpcMiningOperation>().Single().PickupAt;
    }

    private static long Ore(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return world.Set<WorldAsteroid>().Sum(a => a.Quantity);
    }

    [Fact]
    public void CatalogAdmitsExactMinersHaulersAndFourCompleteResponseRosters()
    {
        using TestWorld db = new();
        Prepare(db);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        var profiles = db.Services.GetRequiredService<NpcMiningCatalog>();
        Assert.Equal(11, profiles.Owners.Count);
        Assert.All(
            profiles.Owners,
            owner =>
            {
                Assert.NotNull(NpcMiningRules.Roster(owner.FactionId));
                Assert.NotNull(owner.StrongBoxType);
            }
        );
        Assert.Equal(27, NpcMiningRules.Types.Distinct().Count());
        foreach (int type in NpcMiningRules.Types)
        {
            var profile = Assert.IsType<NpcMiningHull>(profiles.Hull(catalog, type));
            Assert.True(profile.Hull.Type.Mass > 0);
            Assert.True(profile.Hull.Defense.Structure > 0);
        }
        Assert.Equal(new NpcMiningLaser(16, 1920000, 11000, 40), profiles.Hull(catalog, NpcMiningRules.Venture)!.Laser);
        Assert.Equal(new NpcMiningLaser(16, 840000, 15000, 180), profiles.Hull(catalog, NpcMiningRules.Retriever)!.Laser);
        Assert.Equal(new NpcMiningLaser(16, 760000, 15000, 180), profiles.Hull(catalog, NpcMiningRules.Skiff)!.Laser);
        Assert.Contains(profiles.Hull(catalog, 42845)!.Hull.ElectronicWarfare, a => a.Kind == NpcEwarKind.GuidanceDisruption);
        Assert.Contains(profiles.Hull(catalog, 43567)!.Hull.ElectronicWarfare, a => a.Kind == NpcEwarKind.Jamming);
        Assert.Contains(profiles.Hull(catalog, 43577)!.Hull.ElectronicWarfare, a => a.Kind == NpcEwarKind.SensorDampening);
        Assert.Contains(profiles.Hull(catalog, 43588)!.Hull.ElectronicWarfare, a => a.Kind == NpcEwarKind.TargetPainting);
    }

    [Theory]
    [InlineData(NpcEmpireFaction.Amarr, false)]
    [InlineData(NpcEmpireFaction.Caldari, false)]
    [InlineData(NpcEmpireFaction.Gallente, false)]
    [InlineData(NpcEmpireFaction.Minmatar, false)]
    [InlineData(NpcEmpireFaction.Amarr, true)]
    [InlineData(NpcEmpireFaction.Caldari, true)]
    [InlineData(NpcEmpireFaction.Gallente, true)]
    [InlineData(NpcEmpireFaction.Minmatar, true)]
    public void MiningDepletesRealOreOnceAndSurvivesReopen(NpcEmpireFaction empire, bool live)
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true, liveWorld: live);
        Prepare(db, empire);
        var miners = Read(db, clock.Now);
        Assert.Equal(3, miners.Length);
        Assert.All(
            miners,
            n =>
            {
                Assert.Equal(NpcRole.MiningMiner, n.Role);
                Assert.Equal((int)empire, n.FactionId);
                Assert.StartsWith("\u2666", n.Name);
                Assert.Equal(NpcActivityKind.Mining, n.Activity!.Kind);
                Assert.Null(n.Weapon);
            }
        );
        Assert.Equal(miners, Read(db, clock.Now));
        long before = Ore(db);
        long due = MiningDue(db);
        clock.AdvanceWallTime(TimeSpan.FromTicks(due - clock.Now));
        var after = Read(db, clock.Now);
        Assert.Equal(before - 480, Ore(db));
        Assert.Equal(after, Read(db, clock.Now));
        long nextDue = MiningDue(db);
        _ = WorldInitialization.Open(db.Path);
        Assert.Equal(after.Select(n => n.Id), Read(db, clock.Now).Select(n => n.Id));
        Assert.Equal(before - 480, Ore(db));
        Assert.Equal(nextDue, MiningDue(db));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(480, saved.Set<WorldNpcMiningCargo>().Sum(c => c.Quantity));
        Assert.Single(saved.Set<WorldNpcMiningOperation>());
        Assert.Empty(saved.Set<NpcAggression>());
        var wire = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(Ballpark.AddNpcs(after, 1))), 3);
        foreach (var item in Assert.IsType<List<object?>>(wire[1]))
        {
            var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(item).State);
            Assert.Equal(-5d, fields["hostile_response_threshold"]);
            Assert.Equal(5d, fields["friendly_response_threshold"]);
        }
        Assert.IsType<DestinyCommand.TimedSpecialEffect>(NpcActivityWire.Effect(after[0].Activity!, true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HaulerWarpsInCollectsOnlyRealCargoAndDeliversWithoutDuplication(bool live, bool stationless)
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true, liveWorld: live);
        Prepare(db);
        if (stationless)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
            catalog
                .Set<Station>()
                .Where(s => s.SolarSystemId == SystemId)
                .ExecuteUpdate(s => s.SetProperty(station => station.SolarSystemId, 30000144));
        }
        Read(db, clock.Now);
        clock.AdvanceWallTime(TimeSpan.FromTicks(PickupAt(db) - clock.Now));
        var hauler = Assert.Single(Read(db, clock.Now), n => n.Role == NpcRole.MiningHauler);
        Assert.NotNull(hauler.Warp);
        using (WorldDbContext saved = new(db.Path))
        {
            Assert.Equal(stationless, saved.Set<WorldNpcMiningOperation>().Single().StationId is null);
        }
        Assert.Equal((byte)NavigationMode.Warp, Ballpark.AddNpcs([hauler], 1).State[13]);
        long arrival;
        using (WorldDbContext world = new(db.Path))
        {
            arrival = world.Set<WorldNpcMiningMember>().Find(hauler.Id)!.WarpArrivesAt!.Value;
        }
        clock.AdvanceWallTime(TimeSpan.FromTicks(arrival - clock.Now));
        bool departing = false;
        for (int tick = 1; tick <= 60; tick++)
        {
            var current = Read(db, clock.Now).Single(n => n.Id == hauler.Id);
            using WorldDbContext saved = new(db.Path);
            var member = saved.Set<WorldNpcMiningMember>().Find(hauler.Id)!;
            if (member.Travel == NpcMiningTravel.Outbound)
            {
                Assert.NotNull(current.Warp);
                Assert.True(saved.Set<WorldNpcMiningCargo>().Where(c => c.NpcId == hauler.Id).Sum(c => c.Quantity) > 0);
                arrival = member.WarpArrivesAt!.Value;
                departing = true;
                break;
            }
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            db.Store.Advance(SystemId, tick, clock.Now, [], []);
        }
        Assert.True(departing);
        _ = WorldInitialization.Open(db.Path);
        clock.AdvanceWallTime(TimeSpan.FromTicks(arrival - clock.Now));
        Assert.DoesNotContain(Read(db, clock.Now), n => n.Id == hauler.Id);
        using (WorldDbContext saved = new(db.Path))
        {
            Assert.NotNull(saved.Set<WorldNpc>().Find(hauler.Id)!.RetiredAt);
            Assert.Empty(saved.Set<WorldWreck>());
            Assert.Equal(0, saved.Set<WorldNpcMiningCargo>().Sum(c => c.Quantity));
        }
        Assert.DoesNotContain(Read(db, clock.Now), n => n.Role == NpcRole.MiningHauler);
        clock.AdvanceWallTime(TimeSpan.FromTicks(PickupAt(db) - clock.Now));
        var transport = Assert.Single(Read(db, clock.Now), n => n.Role == NpcRole.MiningHauler);
        Assert.Equal(42657, transport.TypeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaidAttackTriggersOneDelayedResponseAndCivilianEscape(bool live)
    {
        using CombatFixture f = new(liveWorld: live, useTestClock: true);
        Prepare(f.Db);
        SeedDirect(f.Db, f.Clock.Now);
        var miner = Read(f.Db, f.Clock.Now)[0];
        f.Db.Store.SetPosition(ShipId, miner.Position + new SpacePosition(1000, 0, 0), SystemId);
        Assert.Throws<InvalidDataException>(() => f.Weapons.Fire(PilotId, f.Module, miner.Id, SpacePosition.Zero, f.Clock.Now, 1));
        using (WorldDbContext rejected = new(f.Db.Path))
        {
            Assert.Equal(NpcMiningStage.Working, rejected.Set<WorldNpcMiningOperation>().Single().Stage);
        }
        f.Weapons.Fire(PilotId, f.Module, miner.Id, SpacePosition.Zero, f.Clock.Now, 0);
        long response;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var operation = world.Set<WorldNpcMiningOperation>().Single();
            Assert.Equal(NpcMiningStage.Alarm, operation.Stage);
            response = operation.ResponseAt!.Value;
            Assert.Equal(ShipId, operation.ThreatShipId);
            Assert.Empty(world.Set<NpcAggression>());
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        SeedDirect(f.Db, f.Clock.Now);
        var fleeing = Read(f.Db, f.Clock.Now);
        Assert.All(fleeing, n => Assert.Equal(NpcNavigationState.Warping, n.Navigation!.State));
        Assert.DoesNotContain(fleeing, n => n.Role == NpcRole.MiningDefender);
        Assert.Throws<InvalidDataException>(() => f.Weapons.Fire(PilotId, f.Module, miner.Id, SpacePosition.Zero, f.Clock.Now, 0));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(response - f.Clock.Now));
        SeedDirect(f.Db, f.Clock.Now);
        var defense = Read(f.Db, f.Clock.Now).Where(n => n.Role is NpcRole.MiningDefender or NpcRole.MiningLogistics).ToArray();
        Assert.Equal(8, defense.Length);
        Assert.Single(defense, n => n.Role == NpcRole.MiningLogistics);
        SeedDirect(f.Db, f.Clock.Now);
        Assert.Equal(8, Read(f.Db, f.Clock.Now).Count(n => n.Role is NpcRole.MiningDefender or NpcRole.MiningLogistics));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(7, saved.Set<NpcAggression>().Count());
        Assert.All(saved.Set<NpcAggression>(), a => Assert.Equal(ShipId, a.TargetShipId));
        Assert.Empty(saved.Set<NpcAggression>().Where(a => a.NpcId == miner.Id));
    }

    [Fact]
    public void PoorCorporationStandingAlarmsWithoutAnUnprovokedPirateAttack()
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true);
        Prepare(db);
        var miner = Read(db, clock.Now)[0];
        db.Store.SetPosition(ShipId, miner.Position + new SpacePosition(1000, 0, 0), SystemId);
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedNpcStanding
                {
                    PilotId = PilotId,
                    SourceId = miner.CorporationId,
                    Value = -5,
                }
            );
            world.SaveChanges();
        }
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Read(db, clock.Now);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(NpcMiningStage.Alarm, saved.Set<WorldNpcMiningOperation>().Single().Stage);
        Assert.Empty(saved.Set<NpcAggression>());
    }

    [Theory]
    [InlineData(NpcEmpireFaction.Amarr, true, 42.75)]
    [InlineData(NpcEmpireFaction.Caldari, false, 58.5)]
    [InlineData(NpcEmpireFaction.Gallente, true, 38.25)]
    [InlineData(NpcEmpireFaction.Minmatar, false, 65.25)]
    public void LogisticsPaysCatalogCapacitorAndRepairsOnlyMissingHealthOncePerCycle(NpcEmpireFaction empire, bool armor, double cost)
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true);
        Prepare(db, empire);
        var miner = Read(db, clock.Now)[0];
        using (WorldDbContext world = new(db.Path))
        {
            NpcMiningSeeder.Alert(world, world.Set<WorldNpc>().Find(miner.Id)!, ShipId, clock.Now);
            world.SaveChanges();
        }
        clock.AdvanceWallTime(TimeSpan.FromSeconds(NpcMiningRules.ResponseSeconds));
        Read(db, clock.Now);
        long arrival;
        using (WorldDbContext world = new(db.Path))
        {
            arrival = world.Set<WorldNpcMiningMember>().Max(m => m.WarpArrivesAt)!.Value;
        }
        clock.AdvanceWallTime(TimeSpan.FromTicks(arrival - clock.Now));
        var defense = Read(db, clock.Now);
        var target = defense.First(n => n.Role == NpcRole.MiningDefender);
        var healer = defense.Single(n => n.Role == NpcRole.MiningLogistics);
        double charge;
        using (WorldDbContext world = new(db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(target.Id)!;
            if (armor)
            {
                npc.ArmorDamage = 100;
            }
            else
            {
                npc.ShieldCharge = target.Health.Defense.Shield - 100;
                npc.UpdatedAt = clock.Now;
            }
            charge = world.Set<WorldNpcMiningMember>().Find(healer.Id)!.Capacitor;
            world.SaveChanges();
        }
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        var healed = Read(db, clock.Now);
        Assert.Equal(
            armor ? NpcActivityKind.ArmorRepair : NpcActivityKind.ShieldRepair,
            healed.Single(n => n.Id == healer.Id).Activity!.Kind
        );
        using (WorldDbContext world = new(db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(target.Id)!;
            Assert.Equal(armor ? 0 : target.Health.Defense.Shield, armor ? npc.ArmorDamage : npc.ShieldCharge, 6);
            Assert.Equal(charge - cost, world.Set<WorldNpcMiningMember>().Find(healer.Id)!.Capacitor, 6);
        }
        Assert.Equal(healed, Read(db, clock.Now));
    }

    [Fact]
    public void KillsTransferHarvestedOreToWreckAndLoseOnlyCorporationStanding()
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true);
        Prepare(db);
        var miner = Read(db, clock.Now)[0];
        clock.AdvanceWallTime(TimeSpan.FromTicks(MiningDue(db) - clock.Now));
        Read(db, clock.Now);
        using (WorldDbContext world = new(db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            var npc = world.Set<WorldNpc>().Find(miner.Id)!;
            var result = NpcDamagePersistence.Apply(
                world,
                catalog,
                npc,
                world.Pilots.Find(PilotId)!,
                ShipId,
                PilotFittingStore.ReadAttributes(catalog, npc.TypeId),
                new(1e9, 0, 0, 0),
                clock.Now
            );
            Assert.True(result.Destroyed);
            Assert.NotNull(result.Reward);
            world.SaveChanges();
            transaction.Commit();
        }
        _ = WorldInitialization.Open(db.Path);
        using WorldDbContext saved = new(db.Path);
        var standing = Assert.Single(saved.Set<SavedNpcStanding>());
        Assert.Equal(miner.CorporationId, standing.SourceId);
        Assert.Equal(-0.00667, standing.Value, 6);
        var wreck = Assert.Single(saved.Set<WorldWreck>());
        Assert.Equal(160, Assert.Single(saved.Set<OwnedItem>().Where(i => i.LocationId == wreck.Id)).Quantity);
        Assert.Equal(0, saved.Set<WorldNpcMiningCargo>().Single(c => c.NpcId == miner.Id).Quantity);
        Assert.Empty(saved.Set<PendingSecurityReward>());
    }

    [Theory]
    [InlineData(0.449)]
    [InlineData(0)]
    [InlineData(-0.5)]
    public void HighSecMilestoneDoesNotSeedLowOrNullSecurity(double security)
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db, security: security);
        Assert.Empty(Read(db, Clock().Now));
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(-2, 2, true)]
    [InlineData(-2, 3, false)]
    public void TransportUsesCatalogWarpCoreStrength(double status, double strength, bool expected) =>
        Assert.Equal(expected, NpcMiningRules.CanWarp(status, [strength]));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(10000, 100)]
    [InlineData(20000, 50)]
    public void RepairFalloffUsesCatalogOptimalAndFalloff(double distance, double expected) =>
        Assert.Equal(expected, NpcMiningRules.RepairAmount(100, 10000, 10000, distance), 9);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MiningSchemaCanUpgradeAnOlderOrIndependentlyUpgradedSave(bool current)
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldNpcMiningCargo; DROP TABLE WorldNpcMiningMember; DROP TABLE WorldNpcMiningOperation;"
            );
            if (!current)
            {
                world.Database.ExecuteSqlRaw("PRAGMA user_version=82");
            }
        }
        _ = WorldInitialization.Open(db.Path);
        _ = WorldInitialization.Open(db.Path);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Empty(saved.Set<WorldNpcMiningOperation>());
        Assert.Empty(saved.Set<WorldNpcMiningMember>());
        Assert.Empty(saved.Set<WorldNpcMiningCargo>());
        Assert.Equal(2, saved.Pilots.Count());
    }

    [Theory]
    [InlineData(83)]
    [InlineData(84)]
    [InlineData(WorldMigration.CurrentVersion)]
    public void ExistingMiningTableWithoutHaulGenerationUpgradesWithoutLosingProgress(int version)
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true);
        Prepare(db);
        var contacts = Read(db, clock.Now);
        long due = MiningDue(db);
        clock.AdvanceWallTime(TimeSpan.FromTicks(due - clock.Now));
        Read(db, clock.Now);
        long ore = Ore(db);
        WorldNpcMiningOperation operation;
        WorldNpcMiningMember[] members;
        WorldNpcMiningCargo[] cargo;
        using (WorldDbContext world = new(db.Path))
        {
            operation = world.Set<WorldNpcMiningOperation>().AsNoTracking().Single();
            members = world.Set<WorldNpcMiningMember>().AsNoTracking().OrderBy(m => m.NpcId).ToArray();
            cargo = world.Set<WorldNpcMiningCargo>().AsNoTracking().OrderBy(c => c.NpcId).ThenBy(c => c.TypeId).ToArray();
            Assert.NotEmpty(cargo);
            Assert.All(cargo, c => Assert.True(c.Quantity > 0));
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldNpcMiningOperation DROP COLUMN HaulGeneration");
            string schemaVersion = $"PRAGMA user_version={version}";
            world.Database.ExecuteSqlRaw(schemaVersion);
        }
        _ = WorldInitialization.Open(db.Path);
        _ = WorldInitialization.Open(db.Path);
        using (WorldDbContext saved = new(db.Path))
        {
            var upgraded = saved.Set<WorldNpcMiningOperation>().Single();
            Assert.Equal(0, upgraded.HaulGeneration);
            Assert.Equal(JsonSerializer.Serialize(operation), JsonSerializer.Serialize(upgraded));
            Assert.Equal(
                JsonSerializer.Serialize(members),
                JsonSerializer.Serialize(saved.Set<WorldNpcMiningMember>().OrderBy(m => m.NpcId).ToArray())
            );
            Assert.Equal(
                JsonSerializer.Serialize(cargo),
                JsonSerializer.Serialize(saved.Set<WorldNpcMiningCargo>().OrderBy(c => c.NpcId).ThenBy(c => c.TypeId).ToArray())
            );
            Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
            upgraded.HaulGeneration = 7;
            saved.SaveChanges();
        }
        _ = WorldInitialization.Open(db.Path);
        using WorldDbContext reopened = new(db.Path);
        Assert.Equal(7, reopened.Set<WorldNpcMiningOperation>().Single().HaulGeneration);
        Assert.Equal(ore, Ore(db));
        Assert.Equal(contacts.Select(n => n.Id), Read(db, clock.Now).Select(n => n.Id));
        Assert.Equal(2, reopened.Pilots.Count());
    }

    [Fact]
    public void DepletedOreCannotBecomeNegativeOrRegenerateOnReopen()
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true);
        Prepare(db);
        var miner = Read(db, clock.Now)[0];
        using (WorldDbContext world = new(db.Path))
        {
            long rock = world.Set<WorldNpcMiningMember>().Find(miner.Id)!.AsteroidId!.Value;
            world.Set<WorldAsteroid>().ExecuteUpdate(s => s.SetProperty(a => a.Quantity, a => a.Id == rock ? 5 : 0));
        }
        clock.AdvanceWallTime(TimeSpan.FromTicks(MiningDue(db) - clock.Now));
        Read(db, clock.Now);
        Assert.Equal(0, Ore(db));
        _ = WorldInitialization.Open(db.Path);
        Read(db, clock.Now);
        Assert.Equal(0, Ore(db));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(5, saved.Set<WorldNpcMiningCargo>().Sum(c => c.Quantity));
        Assert.DoesNotContain(saved.Set<WorldAsteroid>(), a => a.Quantity < 0);
    }

    [Fact]
    public void MiningCannotOverfillItsCatalogHold()
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true);
        Prepare(db);
        var miner = Read(db, clock.Now)[0];
        long capacity;
        using (WorldDbContext world = new(db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath))
        {
            capacity = checked((long)(catalog.Set<ItemType>().Find(miner.TypeId)!.Capacity!.Value / 0.1));
            world.Add(
                new WorldNpcMiningCargo
                {
                    NpcId = miner.Id,
                    TypeId = 1230,
                    Quantity = capacity - 1,
                }
            );
            world.SaveChanges();
        }
        clock.AdvanceWallTime(TimeSpan.FromTicks(MiningDue(db) - clock.Now));
        Read(db, clock.Now);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(capacity, saved.Set<WorldNpcMiningCargo>().Find(miner.Id, 1230)!.Quantity);
        Assert.Null(saved.Set<WorldNpcMiningMember>().Find(miner.Id)!.MiningDueAt);
    }

    [Fact]
    public void GuidanceDisruptionChangesMissileChargesAndLeavesTurretsAlone()
    {
        using TestWorld db = new();
        Prepare(db);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        var ability = db
            .Services.GetRequiredService<NpcMiningCatalog>()
            .Hull(catalog, 42845)!
            .Hull.ElectronicWarfare.Single(a => a.Kind == NpcEwarKind.GuidanceDisruption);
        NpcEwarContact contact = new(10, 42845, 1, ability, ability.Power, 0, 100000000);
        DogmaGraph graph = new(
            [
                new(1, new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.WeaponDisruptionResistance] = 1 }),
                new(
                    2,
                    new Dictionary<DogmaAttributeId, double>
                    {
                        [DogmaAttributeId.MaxVelocity] = 1000,
                        [DogmaAttributeId.MissileFlightTime] = 10000,
                        [DogmaAttributeId.ExplosionRadius] = 50,
                        [DogmaAttributeId.ExplosionVelocity] = 150,
                    }
                ),
                new(3, new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.MaxRange] = 5000 }),
            ],
            NpcEwarRules.Modifiers(1, [3], [contact], [2]).ToArray(),
            new Dictionary<DogmaAttributeId, DogmaAttributeDefinition>()
        );
        Assert.Equal(853.90625, graph.Attributes(2)[DogmaAttributeId.MaxVelocity], 6);
        Assert.Equal(8539.0625, graph.Attributes(2)[DogmaAttributeId.MissileFlightTime], 6);
        Assert.Equal(59.453125, graph.Attributes(2)[DogmaAttributeId.ExplosionRadius], 6);
        Assert.Equal(121.640625, graph.Attributes(2)[DogmaAttributeId.ExplosionVelocity], 6);
        Assert.Equal(5000, graph.Attributes(3)[DogmaAttributeId.MaxRange]);
        Assert.Equal("ewGuidanceDisrupt", NpcEwarWire.Slot(ability.Kind));
    }

    [Fact]
    public void AlarmRetiresFleetThenRestartsOneGenerationAfterCooldown()
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true);
        Prepare(db);
        var initial = Read(db, clock.Now);
        using (WorldDbContext world = new(db.Path))
        {
            NpcMiningSeeder.Alert(world, world.Set<WorldNpc>().Find(initial[0].Id)!, ShipId, clock.Now);
            world.SaveChanges();
        }
        clock.AdvanceWallTime(TimeSpan.FromSeconds(NpcMiningRules.ResponseSeconds));
        Assert.Equal(11, Read(db, clock.Now).Length);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(NpcMiningRules.DefenseLifetimeSeconds - NpcMiningRules.ResponseSeconds));
        Assert.Empty(Read(db, clock.Now));
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(NpcMiningStage.Cooldown, world.Set<WorldNpcMiningOperation>().Single().Stage);
            Assert.Empty(world.Set<NpcAggression>());
        }
        _ = WorldInitialization.Open(db.Path);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(NpcMiningRules.CooldownSeconds));
        var replacement = Read(db, clock.Now);
        Assert.Equal(3, replacement.Length);
        Assert.DoesNotContain(replacement, n => initial.Any(old => old.Id == n.Id));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(1, saved.Set<WorldNpcEncounter>().Single(e => e.Role == NpcRole.MiningMiner).Generation);
        Assert.Single(saved.Set<WorldNpcMiningOperation>());
    }

    [Fact]
    public async Task ConcurrentVisitorsCannotCreateDuplicateOperationsOrMiningCycles()
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true);
        Prepare(db);
        var views = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => Read(db, clock.Now))));
        Assert.All(views, view => Assert.Equal(views[0], view));
        using WorldDbContext saved = new(db.Path);
        Assert.Single(saved.Set<WorldNpcMiningOperation>());
        Assert.Equal(3, saved.Set<WorldNpcMiningMember>().Count());
    }

    [Fact]
    public void ThreeActualTacklePointsHoldTransportAndTwoAllowEscape()
    {
        using var f = TackleTests.Prepare(447, 3242);
        long scrambler = f.Fit(447);
        long disruptor = f.Fit(3242);
        Prepare(f.Db);
        SeedDirect(f.Db, f.Clock.Now);
        var target = Read(f.Db, f.Clock.Now)[0];
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(target.Id)!;
            npc.TypeId = 42657; // Catalog Bustard, with warpScrambleStatus=-2.
            npc.Role = NpcRole.MiningHauler;
            var member = world.Set<WorldNpcMiningMember>().Find(npc.Id)!;
            member.Travel = NpcMiningTravel.Collecting;
            member.MiningDueAt = null;
            member.MiningStartedAt = null;
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(ShipId, target.Position + new SpacePosition(1000, 0, 0), SystemId);
        var tackle = f.Db.Services.GetRequiredService<IPilotTackle>();
        tackle.Activate(
            PilotId,
            scrambler,
            DogmaEffect.WarpScrambler,
            new(target.Id, TackleTargetKind.Npc),
            new HashSet<long> { target.Id },
            f.Clock.Now
        );
        tackle.Activate(
            PilotId,
            disruptor,
            DogmaEffect.WarpDisruptor,
            new(target.Id, TackleTargetKind.Npc),
            new HashSet<long> { target.Id },
            f.Clock.Now
        );
        SeedDirect(f.Db, f.Clock.Now);
        Assert.Null(Read(f.Db, f.Clock.Now).Single(n => n.Id == target.Id).Warp);
        tackle.Stop(PilotId, ShipId, disruptor);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        SeedDirect(f.Db, f.Clock.Now);
        Assert.NotNull(Read(f.Db, f.Clock.Now).Single(n => n.Id == target.Id).Warp);
    }

    [Fact]
    public void ArrivedCaldariResponseLaunchesCatalogMissilesAtTheAggressor()
    {
        using CombatFixture f = new(useTestClock: true);
        Prepare(f.Db);
        SeedDirect(f.Db, f.Clock.Now);
        var miner = Read(f.Db, f.Clock.Now)[0];
        f.Db.Store.SetPosition(ShipId, miner.Position + new SpacePosition(1000, 0, 0), SystemId);
        f.Weapons.Fire(PilotId, f.Module, miner.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(NpcMiningRules.ResponseSeconds));
        SeedDirect(f.Db, f.Clock.Now);
        long arrival;
        using (WorldDbContext world = new(f.Db.Path))
        {
            arrival = world.Set<WorldNpcMiningMember>().Max(m => m.WarpArrivesAt)!.Value;
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(arrival - f.Clock.Now));
        SeedDirect(f.Db, f.Clock.Now);
        var defender = Read(f.Db, f.Clock.Now).First(n => n.TypeId == 43563);
        var combat = f.Db.Services.GetRequiredService<INpcCombat>();
        Assert.Null(combat.Fire(PilotId, ShipId, defender.Id, SpacePosition.Zero, f.Clock.Now, 0.5, 1));
        using WorldDbContext saved = new(f.Db.Path);
        var missile = Assert.Single(saved.Set<WorldMissile>().Where(m => m.SourceShipId == defender.Id));
        Assert.Equal(210, missile.ChargeTypeId);
        Assert.Equal(ShipId, missile.TargetId);
    }

    [Fact]
    public void SnapshotReplaysMiningBeamsAndEscapeStopsThemBeforeWarpOrders()
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true);
        Prepare(db);
        var miners = Read(db, clock.Now);
        db.Store.SetPosition(ShipId, miners[0].Position + new SpacePosition(1000, 0, 0), SystemId);
        using FlightSession flight = new(
            db.Store,
            db.Store.FindPilot(1000001)!,
            clock,
            new(),
            db.Services.GetRequiredService<IAsteroidField>().List(SystemId),
            encounters: db.Services.GetRequiredService<INpcEncounters>()
        );
        flight.Snapshot();
        Assert.Equal(
            3,
            flight
                .Updates.Select(u => u.Command)
                .OfType<DestinyCommand.TimedSpecialEffect>()
                .Count(e => e.Graphic == "effects.Laser" && e.Start)
        );
        using (WorldDbContext world = new(db.Path))
        {
            NpcMiningSeeder.Alert(world, world.Set<WorldNpc>().Find(miners[0].Id)!, ShipId, clock.Now);
            world.SaveChanges();
        }
        flight.Updates.Clear();
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        flight.RefreshNpcs();
        var states = flight.Updates.Select(u => u.Command).OfType<DestinyCommand.AddBalls>().ToArray();
        Assert.Equal(3, states.Length);
        Assert.All(states.SelectMany(s => s.Addition.Slims).OfType<NpcSlimItem>(), n => Assert.NotNull(n.Npc.Warp));
        foreach (var miner in miners)
        {
            int stop = flight.Updates.FindIndex(u =>
                u.Command is DestinyCommand.TimedSpecialEffect e && e.Id == miner.Id && e.Graphic == "effects.Laser" && !e.Start
            );
            int warp = flight.Updates.FindIndex(u =>
                u.Command is DestinyCommand.AddBalls a && a.Addition.Slims.OfType<NpcSlimItem>().Any(n => n.Npc.Id == miner.Id)
            );
            Assert.True(stop >= 0 && stop < warp);
            Assert.Contains(
                flight.Updates,
                u => u.Command is DestinyCommand.SpecialEffect e && e.Id == miner.Id && e.Graphic == "effects.Warping" && e.Start
            );
        }
    }
}
