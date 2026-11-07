using Eden.Data.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal static class NpcMiningSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldNpcMiningOperation(
                EncounterId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldNpcEncounter(Id) ON DELETE CASCADE,
                SystemId INTEGER NOT NULL, BeltId INTEGER NOT NULL, CorporationId INTEGER NOT NULL, FactionId INTEGER NOT NULL,
                StationId INTEGER, Stage INTEGER NOT NULL, PickupAt INTEGER NOT NULL, HaulGeneration INTEGER NOT NULL DEFAULT 0,
                ThreatShipId INTEGER,
                ResponseAt INTEGER, ResetAt INTEGER, AlertX REAL NOT NULL, AlertY REAL NOT NULL, AlertZ REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldNpcMiningOperation_SystemId ON WorldNpcMiningOperation(SystemId);
            CREATE TABLE IF NOT EXISTS WorldNpcMiningMember(
                NpcId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldNpc(Id) ON DELETE CASCADE,
                EncounterId INTEGER NOT NULL REFERENCES WorldNpcMiningOperation(EncounterId) ON DELETE CASCADE,
                Travel INTEGER NOT NULL, AsteroidId INTEGER, MiningStartedAt INTEGER, MiningDueAt INTEGER,
                Capacitor REAL NOT NULL, CapacitorAt INTEGER NOT NULL, WarpStartedAt INTEGER, WarpArrivesAt INTEGER,
                WarpOriginX REAL NOT NULL, WarpOriginY REAL NOT NULL, WarpOriginZ REAL NOT NULL, WarpSpeed REAL NOT NULL,
                SupportTargetId INTEGER, SupportStartedAt INTEGER NOT NULL, SupportReadyAt INTEGER NOT NULL, SupportKind INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldNpcMiningMember_EncounterId ON WorldNpcMiningMember(EncounterId);
            CREATE TABLE IF NOT EXISTS WorldNpcMiningCargo(
                NpcId INTEGER NOT NULL REFERENCES WorldNpc(Id) ON DELETE CASCADE, TypeId INTEGER NOT NULL,
                Quantity INTEGER NOT NULL CHECK(Quantity>=0), PRIMARY KEY(NpcId,TypeId));
            """
        );
        // Earlier mining saves already have this table, so CREATE IF NOT EXISTS
        // cannot add the later per-haul counter, including on schema 84 saves.
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldNpcMiningOperation')")
                .Contains("HaulGeneration")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldNpcMiningOperation ADD COLUMN HaulGeneration INTEGER NOT NULL DEFAULT 0");
        }
    }
}
