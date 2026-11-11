using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCapsuleerAgent(
    long PilotId,
    Eden.Gameplay.Capsuleers.CapsuleerCareer Career,
    int CommunitySystemId,
    long FounderModuleId,
    long FounderShipId,
    int Slot,
    long StartingCents
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedCapsuleerAgent(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCapsuleerAgent>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCapsuleerAgent
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCapsuleerAgent.PilotId=json_extract(value,'$.Id')
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
                    INSERT INTO WorldCapsuleerAgent(
                        PilotId,Career,CommunitySystemId,FounderModuleId,FounderShipId,Slot,StartingCents
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.Career'),
                        json_extract(value,'$.CommunitySystemId'),json_extract(value,'$.FounderModuleId'),
                        json_extract(value,'$.FounderShipId'),json_extract(value,'$.Slot'),
                        json_extract(value,'$.StartingCents')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        Career=excluded.Career,CommunitySystemId=excluded.CommunitySystemId,
                        FounderModuleId=excluded.FounderModuleId,FounderShipId=excluded.FounderShipId,Slot=excluded.Slot,
                        StartingCents=excluded.StartingCents
                    """
                );
            }
        }
    }
}
