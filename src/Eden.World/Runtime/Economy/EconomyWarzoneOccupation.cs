using System.Text.Json;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWarzoneOccupation(int SystemId, MilitiaFaction Occupier, int VictoryPoints, long? CapturedAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterWarzoneOccupation() =>
        Register(
            new EconomyTable<SavedWarzoneOccupation, EconomyWarzoneOccupation>(
                "WorldWarzoneOccupation",
                row => new(row.SystemId),
                row => new(row.SystemId, row.Occupier, row.VictoryPoints, row.CapturedAt),
                state =>
                    new()
                    {
                        SystemId = state.SystemId,
                        Occupier = state.Occupier,
                        VictoryPoints = state.VictoryPoints,
                        CapturedAt = state.CapturedAt,
                    },
                ProjectWarzoneOccupation,
                "SystemId,Occupier,VictoryPoints,CapturedAt",
                reader =>
                    new(
                        reader.GetInt32(1),
                        (MilitiaFaction)reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4)
                    ),
                state => new(state.SystemId),
                primaryColumn: "SystemId"
            )
        );

    private static void ProjectWarzoneOccupation(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWarzoneOccupation>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldWarzoneOccupation WHERE SystemId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldWarzoneOccupation(SystemId,Occupier,VictoryPoints,CapturedAt)
                SELECT json_extract(value,'$.SystemId'), json_extract(value,'$.Occupier'),
                    json_extract(value,'$.VictoryPoints'), json_extract(value,'$.CapturedAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SystemId) DO UPDATE SET
                    Occupier=excluded.Occupier, VictoryPoints=excluded.VictoryPoints, CapturedAt=excluded.CapturedAt
                """
            );
        }
    }
}
