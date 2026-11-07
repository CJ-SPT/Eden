using Eden.Gameplay.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal static class NpcEncounterSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldNpcEncounter(
                Id INTEGER NOT NULL PRIMARY KEY, SystemId INTEGER NOT NULL, AnchorId INTEGER NOT NULL,
                AnchorKind INTEGER NOT NULL, Role INTEGER NOT NULL, FactionId INTEGER NOT NULL,
                HomeX REAL NOT NULL, HomeY REAL NOT NULL, HomeZ REAL NOT NULL,
                Occupied INTEGER NOT NULL, Generation INTEGER NOT NULL, Version INTEGER NOT NULL, NextSpawnAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldNpcEncounter_SystemId ON WorldNpcEncounter(SystemId);
            """
        );
        long sequence = NpcEncounterDefinitions.FirstNpcId - 1;
        world.Database.ExecuteSql($"UPDATE sqlite_sequence SET seq=MAX(seq,{sequence}) WHERE name='WorldNpc'");
        world.Database.ExecuteSql(
            $"""
            INSERT INTO sqlite_sequence(name,seq)
                SELECT 'WorldNpc',{sequence} WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='WorldNpc');
            """
        );
    }
}
