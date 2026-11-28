using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Corporations;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mail;
using Eden.Data.World.Mining;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Tests.Structures;
using Eden.Server;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldMigrationTests
{
    [Fact]
    public void Version241UpgradeAddsCampaignCreditBeforeResidentStartup()
    {
        using TestWorld fixture = new(freshInitialization: true);
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldSovereigntyCampaignCredit; PRAGMA user_version=241;");
        }

        WorldDatabase database = new(fixture.Path);
        WorldInitialization.Initialize(database);
        WorldInitialization.Initialize(database);
        using var reopened = ServerServices.Build(database, liveWorld: true);
        using WorldDbContext saved = new(database.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Empty(saved.Set<SavedSovereigntyCampaignCredit>());
        Assert.Empty(
            reopened.GetRequiredService<LiveWorld>().Economy.Inspect(world => world.Set<SavedSovereigntyCampaignCredit>().ToArray())
        );
    }

    [Fact]
    public void Version230UpgradeLoadsAllThreeNewResidentTablesAndRetainsThePilot()
    {
        using TestWorld db = new(freshInitialization: true);
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldMailContactPolicy;
                DROP TABLE SavedMiningPollutionCloud;
                DROP TABLE WorldIncursionLoyaltyEntry;
                PRAGMA user_version=230;
                """
            );
        }
        WorldDatabase database = new(db.Path);
        WorldInitialization.Initialize(database);
        WorldInitialization.Initialize(database);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(WorldMigration.CurrentVersion, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        }
        using var reopened = ServerServices.Build(database, liveWorld: true);
        using var loaded = reopened.GetRequiredService<LiveWorld>().Economy.Begin();
        Assert.Equal(pilot.ShipId, loaded.Pilots.Find(pilot.PilotId)!.ActiveShipId);
        Assert.Empty(loaded.Set<SavedMailContactPolicy>());
        Assert.Empty(loaded.Set<SavedMiningPollutionCloud>());
        Assert.Empty(loaded.Set<SavedIncursionLoyaltyEntry>());
    }

    [Theory]
    [InlineData(189)]
    [InlineData(213)]
    public void StructureTackleUpgradePreservesShipSourceAndRevisionTriggers(int version)
    {
        using StructureTests.Fixture fixture = new();
        long ship = fixture.ShipId;
        long pilot = fixture.PilotId;
        long module = fixture.Item(35943);
        using WorldDbContext world = new(fixture.Db.Path);
        world.Database.ExecuteSqlRaw(
            """
            DROP TABLE WorldTackle;
            CREATE TABLE WorldTackle(
                ModuleId INTEGER PRIMARY KEY REFERENCES WorldItem(Id), PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id),
                SourceShipId INTEGER NOT NULL REFERENCES WorldShip(Id), TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                TargetId INTEGER NOT NULL, TargetKind INTEGER NOT NULL, Effect INTEGER NOT NULL, DurationMilliseconds REAL NOT NULL,
                CapacitorNeed REAL NOT NULL, Range REAL NOT NULL, Strength REAL NOT NULL, WebPercent REAL NOT NULL,
                MwdBlock REAL NOT NULL, StartedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
            """
        );
        world.Database.ExecuteSqlRaw(version == 189 ? "PRAGMA user_version=189" : "PRAGMA user_version=213");
        ResidentFitSchema.Create(world);
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldTackle VALUES({module},{pilot},{ship},526,30001392,{ship},0,6426,
                10000,5,10000,0,-50,0,100,200)
            """
        );
        using Eden.World.Catalog.CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        WorldMigration.Open(world, catalog, fixture.Clock);
        var upgraded = world.Set<WorldTackle>().Single();
        Assert.Equal(fixture.ShipId, upgraded.SourceShipId);
        Assert.Null(upgraded.SourceStructureId);
        Assert.Equal(-50, upgraded.WebPercent);
        Assert.Equal(200, upgraded.ExpiresAt);
        Assert.True(
            world
                .Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*) AS Value FROM sqlite_schema WHERE tbl_name='WorldTackle' AND name LIKE 'Fit_WorldTackle_%'"
                )
                .Single() > 0
        );
    }

    [Fact]
    public void Version187StructureBattleUpgradePreservesDamageAndSavedDeadlines()
    {
        using StructureTests.Fixture fixture = new();
        var structure = fixture.Online();
        long repairAt = fixture.Clock.Now + TimeSpan.TicksPerMinute;
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Add(
                new SavedStructureBattle
                {
                    StructureId = structure.Id,
                    Shield = 123,
                    Armor = 456,
                    Hull = 789,
                    RepairAt = repairAt,
                }
            );
            world.SaveChanges();
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldStructureEnergy;
                ALTER TABLE WorldStructureBattle DROP COLUMN ShieldCapacity;
                ALTER TABLE WorldStructureBattle DROP COLUMN ArmorCapacity;
                ALTER TABLE WorldStructureBattle DROP COLUMN HullCapacity;
                PRAGMA user_version=187;
                """
            );
        }
        for (int reopen = 0; reopen < 2; reopen++)
        {
            WorldInitialization.Initialize(new(fixture.Db.Path));
            using WorldDbContext world = new(fixture.Db.Path);
            var battle = world.Set<SavedStructureBattle>().Find(structure.Id)!;
            Assert.Null(world.Set<SavedStructureEnergy>().Find(structure.Id));
            Assert.Equal(123, battle.Shield);
            Assert.Equal(456, battle.Armor);
            Assert.Equal(789, battle.Hull);
            Assert.Equal(repairAt, battle.RepairAt);
            Assert.Equal(0, battle.ShieldCapacity);
            Assert.Equal(0, battle.ArmorCapacity);
            Assert.Equal(0, battle.HullCapacity);
            Assert.Equal(WorldMigration.CurrentVersion, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        }
    }

    [Fact]
    public void Version226AddsCorporationWarStateAndPreservesTheExistingWorld()
    {
        using StructureTests.Fixture fixture = new();
        var structure = fixture.Online();
        long ship = fixture.ShipId;
        long cargo = fixture.Item(34, 29);
        var database = new WorldDatabase(fixture.Db.Path);
        using (WorldDbContext world = new(database.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCorporationWarInvitationSettings;
                DROP TABLE WorldCorporationWarInvitation;
                DROP TABLE WorldCorporationWar;
                PRAGMA user_version=226;
                """
            );
        }

        for (int reopen = 0; reopen < 2; reopen++)
        {
            WorldInitialization.Initialize(database, fixture.Clock);
            using (WorldDbContext world = new(database.Path))
            {
                Assert.Equal(WorldMigration.CurrentVersion, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
                var columns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCorporationWar')");
                Assert.Contains("InheritedFromWarId", columns);
                Assert.Contains("InheritedAt", columns);
            }
            using LiveWorld live = new(database, fixture.Clock, _ => { });
            live.Economy.Inspect(world =>
            {
                var pilot = world.Pilots.Find(fixture.PilotId)!;
                Assert.Equal(fixture.CorporationId, pilot.CorporationId);
                Assert.Equal(ship, pilot.ActiveShipId);
                Assert.Equal(fixture.PilotId, world.Set<SavedCorporation>().Find(fixture.CorporationId)!.CeoId);
                var upwell = world.Set<SavedStructure>().Find(structure.Id)!;
                Assert.Equal(fixture.CorporationId, upwell.CorporationId);
                Assert.Equal(structure.State, upwell.State);
                Assert.Equal(29, world.Set<OwnedItem>().Find(cargo)!.Quantity);
                Assert.Empty(world.Set<SavedCorporationWar>());
                Assert.Empty(world.Set<SavedCorporationWarInvitation>());
                Assert.Empty(world.Set<SavedCorporationWarInvitationSettings>());
                return true;
            });
        }
    }

    [Fact]
    public void Version227PreservesExistingWarsAndAddsNegotiationAndReportTables()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, freshInitialization: true);
        var database = new WorldDatabase(fixture.Path);
        using (WorldDbContext world = new(database.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldCorporationWar DROP COLUMN OpenForAllies;
                DROP TABLE WorldCorporationWarNegotiation;
                DROP TABLE WorldCorporationWarAlly;
                DROP TABLE WorldCorporationWarKill;
                PRAGMA user_version=227;
                """
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationWar(Id,AttackerId,DefenderId,DeclaredAt,StartsAt,Mutual)
                VALUES (41,98000000,98000001,{clock.Now - TimeSpan.TicksPerDay},{clock.Now},1);
                """
            );
        }

        for (int reopen = 0; reopen < 2; reopen++)
        {
            WorldInitialization.Initialize(database, clock);
            using LiveWorld live = new(database, clock, _ => { });
            live.Economy.Inspect(world =>
            {
                var war = Assert.Single(world.Set<SavedCorporationWar>());
                Assert.Equal((41L, 98000000, 98000001, clock.Now), (war.Id, war.AttackerId, war.DefenderId, war.StartsAt));
                Assert.True(war.Mutual);
                Assert.False(war.OpenForAllies);
                Assert.Empty(world.Set<SavedCorporationWarNegotiation>());
                Assert.Empty(world.Set<SavedCorporationWarAlly>());
                Assert.Empty(world.Set<SavedCorporationWarKill>());
                return true;
            });
        }
    }

    [Fact]
    public void Version225KeepsExistingNpcRowsWhenAddingPatrolExpiry()
    {
        using Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture fixture = new(useTestClock: true);
        long npcId = fixture.Npc.Id;
        var database = new WorldDatabase(fixture.Db.Path);
        using (WorldDbContext world = new(database.Path))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldNpc DROP COLUMN ExpiresAt; PRAGMA user_version=225;");
        }
        WorldInitialization.Initialize(database);
        using LiveWorld live = new(database, fixture.Clock, _ => { });
        var npc = live.Economy.Inspect(world => world.Set<WorldNpc>().Find(npcId)!);
        Assert.Null(npc.ExpiresAt);
        Assert.Equal(fixture.Npc.TypeId, npc.TypeId);
        Assert.Equal(fixture.Npc.SystemId, npc.SystemId);
        Assert.Equal(fixture.Npc.Position, new SpacePosition(npc.X, npc.Y, npc.Z));
    }

    [Fact]
    public void Version185AddsAnInactiveGateCloakWithoutChangingExistingNavigationDeadlines()
    {
        using TestWorld fixture = new(freshInitialization: true);
        long ship = fixture.Store.FindPilot(1000001)!.ShipId;
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Add(new Eden.Data.World.Propulsion.SpatialNavigationState { ShipId = ship, TargetingReadyAt = 123 });
            world.SaveChanges();
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldSpatialNavigation DROP COLUMN GateCloakUntil; PRAGMA user_version=185;");
        }
        WorldInitialization.Initialize(new(fixture.Path));
        using WorldDbContext reopened = new(fixture.Path);
        var state = reopened.Set<Eden.Data.World.Propulsion.SpatialNavigationState>().Find(ship)!;
        Assert.Equal(0, state.GateCloakUntil);
        Assert.Equal(123, state.TargetingReadyAt);
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void Version140ShipFitTriggersUpgradeAndRemainEffectiveAfterReopen()
    {
        using TestWorld fixture = new(freshInitialization: true);
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TRIGGER Fit_WorldShip_UPDATE_old;
                DROP TRIGGER Fit_WorldShip_UPDATE_new;
                PRAGMA user_version=140;
                """
            );
            ResidentFitSchema.Create(world);
        }
        for (int reopen = 0; reopen < 2; reopen++)
        {
            WorldInitialization.Initialize(new(fixture.Path));
            using ResidentFitCache cache = new(new(fixture.Path));
            using WorldDbContext world = new(fixture.Path);
            using var transaction = world.Database.BeginTransaction();
            Assert.Equal(WorldMigration.CurrentVersion, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
            string? before = world.FitRevision(90000001);
            world.Database.ExecuteSql($"UPDATE WorldShip SET Name={"Reopened " + reopen},StationId=60003760 WHERE Id=1000000000001");
            Assert.Equal(before, world.FitRevision(90000001));
            world.Database.ExecuteSqlRaw("UPDATE WorldShip SET TypeId=TypeId+1 WHERE Id=1000000000001");
            Assert.NotEqual(before, world.FitRevision(90000001));
            transaction.Rollback();
        }
        Assert.Equal(601, fixture.Store.FindPilot(1000001)!.ShipTypeId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(WorldMigration.CurrentVersion + 1)]
    public void UnsupportedVersionLeavesTheSaveUnchanged(int version)
    {
        using TestWorld fixture = new(freshInitialization: true);
        using (var connection = new SqliteConnection($"Data Source={fixture.Path};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version={version}";
            command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(fixture.Path);

        var error = Assert.Throws<InvalidDataException>(() => WorldInitialization.Initialize(new(fixture.Path)));

        Assert.Equal($"Unsupported Eden world version {version}.", error.Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void PreservesEachLegacyWorldVersion(int version)
    {
        var directory = TestDirectory.Create("eden-migrate-");
        try
        {
            var file = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
            TestCatalog.Create(file.CatalogPath);
            CreateLegacy(file.Path, version);
            var world = WorldInitialization.Open(file);
            var pilot = Assert.IsType<DockedPilot>(world.FindPilot(1000001));
            Assert.Equal("Original Pilot", pilot.Name);
            Assert.Equal(version == 1 ? "Ibis" : "Original Ship", pilot.ShipName);
            Assert.Equal(1000000000001, pilot.ShipId);
            Assert.Equal(version >= 4 ? new SpacePosition(1, 2, 3) : null, pilot.Position);
            var wing = Assert.IsType<DockedPilot>(world.FindPilot(1000002));
            Assert.Equal(version == 5 ? "Original Wing" : "Eden Wing", wing.Name);
            Assert.Equal(pilot, WorldInitialization.Open(file).FindPilot(1000001));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void AddsProfilesToExistingWorld(int version)
    {
        using TestWorld db = new(freshInitialization: true);
        var position = new SpacePosition(45, 67, 89);
        db.Store.SetPosition(1000000000001, position);
        using var connection = new SqliteConnection($"Data Source={db.Path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            TestWorld.DropTestingMarketSchema
            + """
                DROP INDEX IX_WorldPilot_Name;
                ALTER TABLE WorldPilot DROP COLUMN Appearance;
                ALTER TABLE WorldPilot DROP COLUMN Portrait;
                DROP TABLE WorldSkillQueue;
                DROP TABLE WorldSkillHistory;
                DROP TABLE WorldPilotSkill;
                DROP TABLE WorldItem;
                DROP TABLE WorldAsteroid; DROP TABLE WorldShipEnergy; DROP TABLE WorldMarketTrade; DROP TABLE WorldMarketOrder; DROP
                    TABLE WorldMarketStation; DROP TABLE WorldWallet; DROP TABLE WorldShipHealth; DROP TABLE WorldWreck; DROP TABLE
                    WorldRepairPayment; DROP TABLE WorldNpcAggression; DROP TABLE WorldShipLoss; DROP TABLE WorldNpc; DROP TABLE
                    WorldNpcBounty; DROP TABLE WorldWeaponCooldown;
                ALTER TABLE WorldAccount DROP COLUMN UserName;
                ALTER TABLE WorldAccount DROP COLUMN CreatedAt;
                ALTER TABLE WorldPilot DROP COLUMN CreatedAt;
                ALTER TABLE WorldPilot DROP COLUMN RaceId;
                ALTER TABLE WorldPilot DROP COLUMN BloodlineId;
                ALTER TABLE WorldPilot DROP COLUMN GenderId;
                ALTER TABLE WorldPilot DROP COLUMN AncestryId;
                ALTER TABLE WorldPilot DROP COLUMN SchoolId;
                """
            + (version == 6 ? "ALTER TABLE WorldPosition DROP COLUMN SystemId;" : "")
            + $"PRAGMA user_version={version}";
        command.CommandText =
            """
                DROP TRIGGER IF EXISTS Fit_WorldContractEscrow_INSERT_new;
                DROP TRIGGER IF EXISTS Fit_WorldContractEscrow_DELETE_old;
                DROP TRIGGER IF EXISTS Fit_WorldContractEscrow_UPDATE_old;
                DROP TRIGGER IF EXISTS Fit_WorldContractEscrow_UPDATE_new;
                """ + command.CommandText;
        command.ExecuteNonQuery();

        var world = WorldInitialization.Open(db.Path);
        var pilot = world.FindPilot(1000001)!;
        Assert.Equal(position, pilot.Position);
        Assert.Equal(30000142, pilot.SystemId);
        Assert.Null(world.FindPilot(1000002)!.Position);
        command.CommandText = "PRAGMA user_version";
        Assert.Equal((long)WorldMigration.CurrentVersion, command.ExecuteScalar());
        Assert.Equal(19, pilot.Profile.SchoolId);
        Assert.Equal("Eden Wing", world.FindPilot(1000002)!.Account.UserName);
    }

    [Fact]
    public void VersionElevenPreservesBothPilotsAndTheirProgressWhenOpeningExtraSlots()
    {
        using TestWorld db = new(freshInitialization: true);
        db.Store.SetPosition(1000000000001, new(5, 6, 7));
        var pilot = db.Store.FindPilot(1000001);
        var wing = db.Store.FindPilot(1000002);
        using var connection = new SqliteConnection($"Data Source={db.Path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            TestWorld.DropTestingMarketSchema
            + """
                DROP INDEX IX_WorldPilot_Name;
                DROP INDEX IX_WorldPilot_AccountId;
                CREATE UNIQUE INDEX IX_WorldPilot_AccountId ON WorldPilot(AccountId);
                ALTER TABLE WorldPilot DROP COLUMN Appearance;
                ALTER TABLE WorldPilot DROP COLUMN Portrait;
                UPDATE WorldPilotSkill SET Points=12345 WHERE PilotId=90000001;
                DROP TABLE WorldItem;
                DROP TABLE WorldAsteroid; DROP TABLE WorldShipEnergy; DROP TABLE WorldMarketTrade; DROP TABLE WorldMarketOrder; DROP TABLE
                    WorldMarketStation; DROP TABLE WorldWallet; DROP TABLE WorldShipHealth; DROP TABLE WorldWreck; DROP TABLE
                    WorldRepairPayment; DROP TABLE WorldNpcAggression; DROP TABLE WorldShipLoss; DROP TABLE WorldNpc; DROP TABLE
                    WorldNpcBounty; DROP TABLE WorldWeaponCooldown;
                PRAGMA user_version=11;
                """;
        command.ExecuteNonQuery();
        var migrated = WorldInitialization.Open(db.Path);
        Assert.Equal(pilot, Assert.Single(migrated.ListPilots(1000001)));
        Assert.Equal(wing, Assert.Single(migrated.ListPilots(1000002)));
        command.CommandText = "SELECT Points FROM WorldPilotSkill WHERE PilotId=90000001";
        Assert.Equal(12345L, command.ExecuteScalar());
        command.CommandText = "SELECT [unique] FROM pragma_index_list('WorldPilot') WHERE name='IX_WorldPilot_AccountId'";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void InvalidCatalogReferenceRollsBackLegacyConversion()
    {
        var directory = TestDirectory.Create("eden-migrate-");
        try
        {
            var file = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
            TestCatalog.Create(file.CatalogPath);
            CreateLegacy(file.Path, 3);
            using var connection = new SqliteConnection($"Data Source={file.Path};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE pilot SET corporation=999 WHERE account=1000001";
            command.ExecuteNonQuery();
            Assert.Throws<InvalidDataException>(() => WorldInitialization.Open(file));
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(3L, command.ExecuteScalar());
            command.CommandText = "SELECT name FROM ship WHERE owner=90000001";
            Assert.Equal("Original Ship", command.ExecuteScalar());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TwoPilotsCanSaveTheirPositionsConcurrently()
    {
        using TestWorld db = new(freshInitialization: true);
        await Task.WhenAll(
            Enumerable
                .Range(1, 2)
                .Select(offset =>
                    Task.Run(() =>
                    {
                        for (int tick = 1; tick <= 8; tick++)
                        {
                            db.Store.SetPosition(1000000000000 + offset, new(tick, offset, 0));
                        }
                    })
                )
        );
        Assert.Equal(new SpacePosition(8, 1, 0), db.Store.FindPilot(1000001)!.Position);
        Assert.Equal(new SpacePosition(8, 2, 0), db.Store.FindPilot(1000002)!.Position);
    }

    private static void CreateLegacy(string path, int version)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        using var stream = typeof(TestWorld).Assembly.GetManifestResourceStream(typeof(TestWorld), "LegacyWorldV3.sql")!;
        using StreamReader reader = new(stream);
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE pilot SET name='Original Pilot'; UPDATE ship SET name='Original Ship'";
        command.ExecuteNonQuery();
        if (version <= 2)
        {
            command.CommandText = "ALTER TABLE ship_type DROP COLUMN capacity; ALTER TABLE ship_type DROP COLUMN turrets;";
            command.ExecuteNonQuery();
        }
        if (version == 1)
        {
            command.CommandText = "ALTER TABLE ship DROP COLUMN name";
            command.ExecuteNonQuery();
        }
        if (version >= 4)
        {
            command.CommandText =
                "CREATE TABLE flight(ship INTEGER PRIMARY KEY REFERENCES ship(id),x REAL,y REAL,z REAL); INSERT INTO "
                + "flight VALUES(1000000000001,1,2,3)";
            command.ExecuteNonQuery();
        }
        if (version == 5)
        {
            command.CommandText = """
                INSERT INTO account VALUES(1000002);
                INSERT INTO pilot VALUES(90000002,1000002,'Original Wing',1000044,1000000000002);
                INSERT INTO ship VALUES(1000000000002,90000002,601,60003760,'Wing Ibis');
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = $"PRAGMA user_version={version}";
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
