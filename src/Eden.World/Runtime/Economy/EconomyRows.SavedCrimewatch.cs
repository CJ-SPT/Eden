using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCrimewatch(
    long PilotId,
    long CriminalActive,
    long CriminalUntil,
    long NpcActive,
    long NpcUntil,
    long PvpActive,
    long PvpUntil,
    long SuspectActive,
    long SuspectUntil,
    int SystemId,
    long WeaponsActive,
    long WeaponsUntil
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCrimewatch(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCrimewatch>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCrimewatch
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCrimewatch.PilotId=json_extract(value,'$.Id')
                    )
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldCrimewatch(
                        PilotId,CriminalActive,CriminalUntil,NpcActive,NpcUntil,PvpActive,PvpUntil,SuspectActive,
                        SuspectUntil,SystemId,WeaponsActive,WeaponsUntil
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.CriminalActive'),
                        json_extract(value,'$.CriminalUntil'),json_extract(value,'$.NpcActive'),
                        json_extract(value,'$.NpcUntil'),json_extract(value,'$.PvpActive'),json_extract(value,'$.PvpUntil'),
                        json_extract(value,'$.SuspectActive'),json_extract(value,'$.SuspectUntil'),
                        json_extract(value,'$.SystemId'),json_extract(value,'$.WeaponsActive'),
                        json_extract(value,'$.WeaponsUntil')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        CriminalActive=excluded.CriminalActive,CriminalUntil=excluded.CriminalUntil,
                        NpcActive=excluded.NpcActive,NpcUntil=excluded.NpcUntil,PvpActive=excluded.PvpActive,
                        PvpUntil=excluded.PvpUntil,SuspectActive=excluded.SuspectActive,SuspectUntil=excluded.SuspectUntil,
                        SystemId=excluded.SystemId,WeaponsActive=excluded.WeaponsActive,WeaponsUntil=excluded.WeaponsUntil
                    """
                );
            }
        }
    }
}
