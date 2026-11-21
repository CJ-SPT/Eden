using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Propulsion;

internal static class StructureTackleSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        if (world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldTackle')").Contains("SourceStructureId"))
        {
            return;
        }
        var triggers = world
            .Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_schema WHERE type='trigger' AND tbl_name='WorldTackle'")
            .ToArray();
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE WorldTackle190(
                ModuleId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                SourceShipId INTEGER REFERENCES WorldShip(Id) ON DELETE CASCADE,
                TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL, TargetId INTEGER NOT NULL, TargetKind INTEGER NOT NULL,
                Effect INTEGER NOT NULL, DurationMilliseconds REAL NOT NULL, CapacitorNeed REAL NOT NULL,
                Range REAL NOT NULL, Strength REAL NOT NULL, WebPercent REAL NOT NULL, MwdBlock REAL NOT NULL,
                StartedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL,
                SourceStructureId INTEGER REFERENCES WorldStructure(Id) ON DELETE CASCADE,
                CHECK ((SourceShipId IS NULL) <> (SourceStructureId IS NULL)));
            INSERT INTO WorldTackle190(ModuleId,PilotId,SourceShipId,TypeId,SystemId,TargetId,TargetKind,Effect,DurationMilliseconds,
                CapacitorNeed,Range,Strength,WebPercent,MwdBlock,StartedAt,ExpiresAt)
                SELECT ModuleId,PilotId,SourceShipId,TypeId,SystemId,TargetId,TargetKind,Effect,DurationMilliseconds,
                    CapacitorNeed,Range,Strength,WebPercent,MwdBlock,StartedAt,ExpiresAt FROM WorldTackle;
            DROP TABLE WorldTackle;
            ALTER TABLE WorldTackle190 RENAME TO WorldTackle;
            CREATE INDEX IX_WorldTackle_TargetId_TargetKind ON WorldTackle(TargetId,TargetKind);
            CREATE INDEX IX_WorldTackle_SourceStructureId ON WorldTackle(SourceStructureId);
            CREATE INDEX IX_WorldTackle_SourceShipId ON WorldTackle(SourceShipId);
            CREATE INDEX IX_WorldTackle_PilotId ON WorldTackle(PilotId);
            """
        );
        foreach (string trigger in triggers)
        {
            world.Database.ExecuteSqlRaw(trigger);
        }
    }
}
