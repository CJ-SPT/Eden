using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Corporations;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Repair;
using Eden.World.Storage.Migrations;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

internal static class WorldMigration
{
    internal const int CurrentVersion = 217;

    public static void Open(
        WorldDbContext world,
        CatalogDbContext catalog,
        TimeProvider clock,
        Eden.Gameplay.Market.WorldEconomySettings? economy = null,
        Eden.Gameplay.Economy.EconomicConfiguration? economics = null,
        WorldSetupApproval? setup = null
    )
    {
        CatalogSources.CheckBuild(catalog);
        using var transaction = world.Database.BeginTransaction();
        int version = world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single();
        if (version is < 0 or > CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported Eden world version {version}.");
        }
        if (setup is { NewWorld: true })
        {
            var settings = setup.Draft.Settings;
            var supply = settings.DevelopmentSupply;
            economy = new(supply.TestingMarket, supply.TestingGrants, supply.InstitutionalStock, settings.FreshUniverse);
        }
        // Every step receives the original version, even after historical PRAGMA writes.
        LegacyWorldMigration.Initialize(world, version, economy?.FreshUniverse == true);
        // An approved setup can resume after the first migration transaction has already committed.
        InitialEconomyMigration.Initialize(world, version, setup is { NewWorld: true } && version != 0 ? null : economy, economics);
        Eden.World.Market.StandardMarketSchema.Upgrade(world);
        WorldVersions06To20.Apply(world, catalog, version);
        if (version is >= 6 and < ScienceSchema.Version)
        {
            ScienceSchema.UpgradeInventory(world);
        }
        WorldVersions21To34.Apply(world, clock, version);
        if (version is >= 6 and < ScienceSchema.Version)
        {
            ScienceSchema.UpgradeJobs(world);
        }
        IndustryContainerSchema.Upgrade(world);
        TimedModuleSchema.Upgrade(world);
        ModuleConditionSchema.Upgrade(world);
        ShipHeatSchema.Upgrade(world);
        NaniteRepairSchema.Upgrade(world);
        if (version < CurrentVersion)
        {
            WorldVersions35To49.Apply(world, version);
            WorldVersions50To68.Apply(world, version);
        }
        Eden.World.Characters.ShipCloneSchema.Upgrade(world);
        if (version is >= 6 and < ScienceSchema.Version)
        {
            ScienceSchema.UpgradeContracts(world);
        }
        // NPC columns must exist before the history backfills materialize current entities.
        NpcSchemaReconciliation.Apply(world, version);
        Eden.World.Exploration.CombatAnomalySchema.Upgrade(world);
        InventoryLookupSchema.Upgrade(world);
        Eden.World.Structures.StructureSchema.Upgrade(world);
        Eden.World.Propulsion.StructureTackleSchema.Upgrade(world);
        SavedWorldValidation.ReconcileAndCheck(world, catalog);
        WorldVersions69To84.Apply(world, version);
        transaction.Commit();
        transaction.Dispose();
        if (version < 78)
        {
            InventoryOwnershipSchema.Upgrade(world);
        }
        using var history = world.Database.BeginTransaction();
        WorldVersions86To92.Apply(world, catalog, clock, version, economy, economics);
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCharacterDeletion(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ReadyAt INTEGER NOT NULL, DeletedAt INTEGER);
            PRAGMA user_version=93;
            """
        );
        WorldVersion94.Apply(world, version);
        if (version < 95)
        {
            RigFittingSchema.Upgrade(world);
        }
        // Saves that advanced beyond the old EWAR migration can still have the original single-column key.
        PlayerEwarSchema.Upgrade(world);
        CorporationStorageSchema.Upgrade(world);
        CorporationCalendarSchema.Upgrade(world);
        RemoteRepairSchema.Upgrade(world);
        WorldConfigurationSchema.Upgrade(world);
        WorldConfigurationStore.Seed(world, catalog, clock, economy?.FreshUniverse == true);
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldPilotHostility')")
                .Contains("LastAttackAt")
        )
        {
            // Existing hostility retains its expiry without inventing a recent attack.
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldPilotHostility ADD COLUMN LastAttackAt INTEGER NOT NULL DEFAULT 0");
        }
        DroneCombatPersistence.Upgrade(world);
        DroneDelegationPersistence.Upgrade(world);
        NpcEnergySchema.Upgrade(world);
        RemoteAssistanceSchema.Upgrade(world);
        JetcanSchema.Upgrade(world);
        SpaceLootRights.Upgrade(world);
        Eden.World.Information.SalvagePersistence.Upgrade(world, catalog);
        NaniteRepairPersistence.Reconcile(world, catalog, clock);
        ModuleConditionPersistence.Reconcile(world, catalog, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        ShipHeatPersistence.ValidateSaved(world);
        Eden.World.Capsuleers.CapsuleerCommunityStore.Initialize(world);
        Eden.World.Capsuleers.CapsuleerProvisioningSchema.Upgrade(world);
        Eden.World.Capsuleers.RegionalConflictSchema.Upgrade(world);

        Eden.World.Capsuleers.CapsuleerPopulationStore.Initialize(world);
        Eden.World.Information.InformationSchema.Upgrade(world);
        Eden.World.Capsuleers.CapsuleerPopulationStore.Reconcile(world, catalog);
        Eden.World.Capsuleers.CapsuleerPopulationStore.ScheduleFounders(world);
        Eden.World.Capsuleers.TrafficSchema.Upgrade(world);
        if (version < 120)
        {
            Eden.World.Capsuleers.CapsuleerNameMigration.Apply(world);
        }
        Eden.World.Market.RegionalMarketSchema.Upgrade(world);
        if (version < 152)
        {
            Eden.World.Market.MarketProgressionStore.RetirePlayerSuppliedInputs(world, catalog);
        }
        if (version < 122)
        {
            Eden.World.Market.MarketProgressionStore.Upgrade(world, catalog);
        }
        Eden.World.Agents.MissionSchema.Upgrade(world);
        ConcordSchema.Upgrade(world);
        Eden.World.Exploration.ProbeScanningSchema.Upgrade(world);
        if (version < 150)
        {
            Eden.World.Economy.EconomicStore.ReconcileIndustryPrices(world, catalog);
        }
        Eden.World.Exploration.HackingSchema.Upgrade(world);
        Eden.World.Exploration.WormholeTravelSchema.Upgrade(world);
        Eden.World.Exploration.SleeperSiteSchema.Upgrade(world);
        Eden.World.Inventory.MutationSchema.Upgrade(world);
        Eden.World.Inventory.CargoContainerSchema.Upgrade(world);
        Eden.World.Exploration.AbyssalSchema.Upgrade(world);
        Eden.World.Mining.MiningSiteSchema.Upgrade(world);
        Eden.World.Propulsion.SpatialNavigationSchema.Upgrade(world);
        ShipSharingSchema.Upgrade(world);
        Eden.World.Flight.CapitalTravelSchema.Upgrade(world);
        InventoryLookupSchema.Upgrade(world);
        if (version < 138)
        {
            world.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO WorldCapsuleerAdmission(PilotId)
                SELECT PilotId FROM WorldCapsuleerHauler
                WHERE json_valid(Progress) AND json_type(Progress,'$.TrafficLane')='object';
                """
            );
        }
        Eden.World.Planetary.PlanetarySchema.Upgrade(world);
        if (version < 141)
        {
            ResidentFitSchema.UpgradeShipChanges(world);
        }
        SavedFittingSchema.Upgrade(world);
        Eden.World.Mining.MiningLedgerSchema.Upgrade(world);
        CorporationContactSchema.Upgrade(world);
        CorporationRecruitmentSchema.Upgrade(world);
        CorporationVotingSchema.Upgrade(world);
        CorporationRemovalSchema.Upgrade(world);
        Eden.World.Alliances.AllianceSchema.Upgrade(world);
        Eden.World.Alliances.AllianceMembershipSchema.Upgrade(world);
        Eden.World.Alliances.AllianceBillingSchema.Upgrade(world);
        Eden.World.Alliances.AllianceSocialSchema.Upgrade(world);
        Eden.World.Sovereignty.SovereigntySchema.Upgrade(world);
        Eden.World.Sovereignty.SovereigntySchema.UpgradeCapture(world, version);
        Eden.World.Sovereignty.SovereigntyWarfareSchema.Upgrade(world);
        Eden.World.Sovereignty.SovereigntyActivitySchema.Upgrade(world, version);
        Eden.World.Sovereignty.SovereigntyResourceSchema.Upgrade(world);
        Eden.World.FactionWarfare.FactionWarzoneSchema.Upgrade(world);
        Eden.World.Characters.ContactOwnerSchema.Upgrade(world);
        Eden.World.Characters.PilotActivitySchema.Upgrade(world);
        Eden.World.Bookmarks.SharedBookmarkSchema.Upgrade(world, catalog);
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldDrone')")
                .AsEnumerable()
                .Contains("WorkJson")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldDrone ADD COLUMN WorkJson TEXT");
        }
        JetcanStore.Expire(world, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        Eden.World.Fleets.CommandBurstSchema.Upgrade(world);
        Eden.World.Propulsion.InterdictionSchema.Upgrade(world);
        Eden.World.Combat.AreaWeaponSchema.Upgrade(world);
        KillRightSchema.Upgrade(world);
        LauncherSchema.Upgrade(world);
        Eden.World.Characters.PersonalSettingsSchema.Upgrade(world);
        if (version < 155)
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP INDEX IF EXISTS IX_WorldNpcBounty_NpcId;
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldNpcBounty_NpcId_PilotId ON WorldNpcBounty(NpcId,PilotId);
                """
            );
        }
        if (version < 188)
        {
            world.Database.ExecuteSqlRaw(
                """
                UPDATE WorldCorporation
                SET DetailsJson = json_set(DetailsJson, '$.ApplicationsEnabled', json('true'))
                WHERE CapsuleerFoundingSystemId IS NOT NULL AND ClosedAt IS NULL
                    AND EXISTS (SELECT 1 FROM WorldCapsuleerAgent WHERE PilotId = WorldCorporation.CeoId);
                """
            );
        }
        if (version < CapsuleerCheckpointMigration.Version)
        {
            CapsuleerCheckpointMigration.RemoveEmbeddedCollision(world);
        }
        world.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS WorldMapStatistics(Id INTEGER PRIMARY KEY CHECK (Id=1), Payload TEXT NOT NULL)"
        );
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldActivityRewardTick(
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                DueAt INTEGER NOT NULL, SkillPoints INTEGER NOT NULL CHECK (SkillPoints >= 0),
                Bounties TEXT NOT NULL, PRIMARY KEY (PilotId,DueAt));
            """
        );
        if (version < DevelopmentMarketOrderMigration.Version)
        {
            DevelopmentMarketOrderMigration.RemoveUnusedAsks(world);
            // Commit the schema and sanitized rows first. A restart retries reclamation if interrupted.
            world.Database.ExecuteSqlRaw($"PRAGMA user_version={CapsuleerCheckpointMigration.Version}");
            history.Commit();
            history.Dispose();
            // The next startup step copies the file into RAM, including freelist pages.
            if (world.Database.SqlQueryRaw<int>("PRAGMA freelist_count").AsEnumerable().Single() > 0)
            {
                world.Database.ExecuteSqlRaw("PRAGMA temp_store=FILE; VACUUM;");
            }
            world.Database.ExecuteSqlRaw($"PRAGMA user_version={CurrentVersion}");
            return;
        }
        world.Database.ExecuteSqlRaw($"PRAGMA user_version={CurrentVersion}");
        history.Commit();
    }
}
