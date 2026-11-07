using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal static class NpcMissileSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        long sequence = Math.Max(
            MissileDefinitions.FirstProjectileId - 1,
            world.Database.SqlQueryRaw<long>("SELECT COALESCE(MAX(seq),0) AS Value FROM sqlite_sequence WHERE name='WorldMissile'").Single()
        );
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldMissile')").Contains("SourceKind"))
        {
            // SourceKind determines which identity is a player ship. Keeping
            // either former ship/NPC FK would reject the opposite direction.
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldMissileNext(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    SourceShipId INTEGER NOT NULL, ModuleId INTEGER NOT NULL,
                    LauncherTypeId INTEGER NOT NULL, ChargeTypeId INTEGER NOT NULL, GroupId INTEGER NOT NULL,
                    SystemId INTEGER NOT NULL, TargetId INTEGER NOT NULL, TargetRadius REAL NOT NULL,
                    CorporationId INTEGER NOT NULL, LaunchStamp INTEGER NOT NULL, LaunchedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL, EndedAt INTEGER,
                    Status INTEGER NOT NULL, Reported INTEGER NOT NULL, Snapshot TEXT NOT NULL, Outcome TEXT,
                    SourceKind INTEGER NOT NULL DEFAULT 0, SourceTypeId INTEGER NOT NULL DEFAULT 0);
                INSERT INTO WorldMissileNext
                    SELECT Id,PilotId,SourceShipId,ModuleId,LauncherTypeId,ChargeTypeId,GroupId,
                        SystemId,TargetId,TargetRadius,CorporationId,LaunchStamp,LaunchedAt,
                        UpdatedAt,ExpiresAt,EndedAt,Status,Reported,Snapshot,Outcome,0,0 FROM WorldMissile;
                DROP TABLE WorldMissile;
                ALTER TABLE WorldMissileNext RENAME TO WorldMissile;
                CREATE INDEX IX_WorldMissile_SystemId_Status_UpdatedAt ON WorldMissile(SystemId,Status,UpdatedAt);
                CREATE INDEX IX_WorldMissile_PilotId_Reported ON WorldMissile(PilotId,Reported);
                """
            );
        }
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldNpcAggression')")
                .Contains("MissileReadyAt")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldNpcAggression ADD COLUMN MissileReadyAt INTEGER NOT NULL DEFAULT 0");
            world.Database.ExecuteSqlRaw("UPDATE WorldNpcAggression SET MissileReadyAt=ReadyAt");
        }
        world.Database.ExecuteSql($"UPDATE sqlite_sequence SET seq=MAX(seq,{sequence}) WHERE name='WorldMissile'");
        world.Database.ExecuteSql(
            $"""
            INSERT INTO sqlite_sequence(name,seq)
                SELECT 'WorldMissile',{sequence} WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='WorldMissile');
            """
        );
    }
}
