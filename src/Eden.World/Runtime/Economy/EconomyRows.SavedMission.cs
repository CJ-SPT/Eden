using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedMission(
    long Id,
    long? AcceptedAt,
    long AgentId,
    long? ClosedAt,
    double CorporationFraction,
    int DestinationStation,
    long ExpiresAt,
    double FactionFraction,
    long IskCents,
    int Level,
    long LoyaltyPoints,
    int MissionId,
    bool ObjectiveComplete,
    long OfferedAt,
    long PilotId,
    Eden.Gameplay.Agents.MissionState State,
    bool Storyline,
    int SystemId,
    int Wave,
    double X,
    double Y,
    double Z,
    long BonusIskCents,
    long BonusWindowTicks
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectSavedMission(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMission>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMission
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldMission.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldMission(
                        Id,AcceptedAt,AgentId,ClosedAt,CorporationFraction,DestinationStation,ExpiresAt,FactionFraction,
                        IskCents,Level,LoyaltyPoints,MissionId,ObjectiveComplete,OfferedAt,PilotId,State,Storyline,SystemId,
                        Wave,X,Y,Z,BonusIskCents,BonusWindowTicks
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.AcceptedAt'),json_extract(value,'$.AgentId'),
                        json_extract(value,'$.ClosedAt'),json_extract(value,'$.CorporationFraction'),
                        json_extract(value,'$.DestinationStation'),json_extract(value,'$.ExpiresAt'),
                        json_extract(value,'$.FactionFraction'),json_extract(value,'$.IskCents'),
                        json_extract(value,'$.Level'),json_extract(value,'$.LoyaltyPoints'),
                        json_extract(value,'$.MissionId'),json_extract(value,'$.ObjectiveComplete'),
                        json_extract(value,'$.OfferedAt'),json_extract(value,'$.PilotId'),json_extract(value,'$.State'),
                        json_extract(value,'$.Storyline'),json_extract(value,'$.SystemId'),json_extract(value,'$.Wave'),
                        json_extract(value,'$.X'),json_extract(value,'$.Y'),json_extract(value,'$.Z'),
                        json_extract(value,'$.BonusIskCents'),json_extract(value,'$.BonusWindowTicks')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        AcceptedAt=excluded.AcceptedAt,AgentId=excluded.AgentId,ClosedAt=excluded.ClosedAt,
                        CorporationFraction=excluded.CorporationFraction,DestinationStation=excluded.DestinationStation,
                        ExpiresAt=excluded.ExpiresAt,FactionFraction=excluded.FactionFraction,IskCents=excluded.IskCents,
                        Level=excluded.Level,LoyaltyPoints=excluded.LoyaltyPoints,MissionId=excluded.MissionId,
                        ObjectiveComplete=excluded.ObjectiveComplete,OfferedAt=excluded.OfferedAt,PilotId=excluded.PilotId,
                        State=excluded.State,Storyline=excluded.Storyline,SystemId=excluded.SystemId,Wave=excluded.Wave,
                        X=excluded.X,Y=excluded.Y,Z=excluded.Z,
                        BonusIskCents=excluded.BonusIskCents,BonusWindowTicks=excluded.BonusWindowTicks
                    """
                );
            }
        }
    }
}
