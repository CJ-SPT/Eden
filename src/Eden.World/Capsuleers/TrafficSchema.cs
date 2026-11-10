using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class TrafficSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedTrafficCorrection>().ToTable("WorldTrafficCorrection").HasKey(value => value.PilotId);
        var identity = model.Entity<SavedTrafficIdentity>();
        identity.ToTable("WorldTrafficIdentity").HasKey(value => value.PilotId);
        // No cascading FK: a deleted pilot must not release a real character for reuse.
        identity.HasIndex(value => value.CharacterId).IsUnique();
        model.Entity<SavedCapsuleerSupplies>().ToTable("WorldCapsuleerSupplies").HasKey(value => value.PilotId);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldTrafficCorrection(
                PilotId INTEGER PRIMARY KEY, ShipId INTEGER NOT NULL, TemplateKillId INTEGER NOT NULL, OriginalHullTypeId INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldTrafficIdentity(
                PilotId INTEGER PRIMARY KEY, CharacterId INTEGER NOT NULL UNIQUE,
                KillId INTEGER NOT NULL, SnapshotId TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldCapsuleerSupplies(PilotId INTEGER PRIMARY KEY, ShipId INTEGER NOT NULL);
            """
        );
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            foreach (string row in operation == "UPDATE" ? new[] { "OLD", "NEW" } : new[] { operation == "DELETE" ? "OLD" : "NEW" })
            {
                // Supply changes invalidate only the owning simulated pilot, including cargo quantities
                // which deliberately do not invalidate ordinary human fitting calculations.
                string sql = $"""
                    CREATE TRIGGER IF NOT EXISTS CapsuleerSupply_{operation}_{row} AFTER {operation} ON WorldItem
                    WHEN {row}.FlagId IN ({(int)InventoryFlag.Cargo},{(int)InventoryFlag.DroneBay})
                        AND EXISTS(SELECT 1 FROM WorldTrafficIdentity WHERE PilotId={row}.OwnerId)
                    BEGIN
                        INSERT INTO WorldFitRevision(PilotId,Stamp) VALUES({row}.OwnerId,hex(randomblob(16)))
                        ON CONFLICT(PilotId) DO UPDATE SET Stamp=excluded.Stamp;
                    END;
                    """;
                world.Database.ExecuteSqlRaw(sql);
            }
        }
    }
}
