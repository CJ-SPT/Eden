using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Corporations;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Repair;
using Eden.World.Storage.Migrations;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

internal static class WorldMigration
{
    internal const int CurrentVersion = 113;

    public static void Open(
        WorldDbContext world,
        CatalogDbContext catalog,
        TimeProvider clock,
        Eden.Gameplay.Market.WorldEconomySettings? economy = null,
        Eden.Gameplay.Economy.EconomicConfiguration? economics = null
    )
    {
        CatalogSources.CheckBuild(catalog);
        using var transaction = world.Database.BeginTransaction();
        int version = world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single();
        if (version is < 0 or > CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported Eden world version {version}.");
        }
        // Every step receives the original version, even after historical PRAGMA writes.
        LegacyWorldMigration.Initialize(world, version);
        InitialEconomyMigration.Initialize(world, version, economy, economics);
        WorldVersions06To20.Apply(world, catalog, version);
        WorldVersions21To34.Apply(world, clock, version);
        TimedModuleSchema.Upgrade(world);
        ModuleConditionSchema.Upgrade(world);
        ShipHeatSchema.Upgrade(world);
        NaniteRepairSchema.Upgrade(world);
        if (version < CurrentVersion)
        {
            WorldVersions35To49.Apply(world, version);
            WorldVersions50To68.Apply(world, version);
        }
        // NPC columns must exist before the history backfills materialize current entities.
        NpcSchemaReconciliation.Apply(world, version);
        Eden.World.Exploration.CombatAnomalySchema.Upgrade(world);
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
        if (version < 96)
        {
            PlayerEwarSchema.Upgrade(world);
        }
        CorporationStorageSchema.Upgrade(world);
        RemoteRepairSchema.Upgrade(world);
        WorldConfigurationSchema.Upgrade(world);
        WorldConfigurationStore.Seed(world, catalog, clock);
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
        Eden.World.Information.InformationSchema.Upgrade(world);
        Eden.World.Information.SalvagePersistence.Upgrade(world, catalog);
        NaniteRepairPersistence.Reconcile(world, catalog, clock);
        ModuleConditionPersistence.Reconcile(world, catalog, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        ShipHeatPersistence.ValidateSaved(world);
        world.Database.ExecuteSqlRaw("PRAGMA user_version=113");
        history.Commit();
    }
}
