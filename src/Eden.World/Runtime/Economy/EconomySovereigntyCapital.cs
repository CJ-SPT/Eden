using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyCapital(
    int AllianceId,
    int? CurrentSystemId,
    int? NewSystemId,
    long? ValidAfter,
    int? PreviousSystemId,
    long CurrentSince
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyCapital() =>
        Register(
            new EconomyTable<SavedSovereigntyCapital, EconomySovereigntyCapital>(
                "WorldSovereigntyCapital",
                row => new(row.AllianceId),
                row => new(row.AllianceId, row.CurrentSystemId, row.NewSystemId, row.ValidAfter, row.PreviousSystemId, row.CurrentSince),
                state => new SavedSovereigntyCapital
                {
                    AllianceId = state.AllianceId,
                    CurrentSystemId = state.CurrentSystemId,
                    NewSystemId = state.NewSystemId,
                    ValidAfter = state.ValidAfter,
                    PreviousSystemId = state.PreviousSystemId,
                    CurrentSince = state.CurrentSince,
                },
                ProjectSovereigntyCapital,
                "AllianceId,CurrentSystemId,NewSystemId,ValidAfter,PreviousSystemId,CurrentSince",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        reader.GetInt64(6)
                    ),
                state => new(state.AllianceId),
                indexes: [],
                primaryColumn: "AllianceId"
            )
        );

    private static void ProjectSovereigntyCapital(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyCapital>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyCapital WHERE AllianceId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyCapital(
                    AllianceId,
                    CurrentSystemId,
                    NewSystemId,
                    ValidAfter,
                    PreviousSystemId,
                    CurrentSince)
                SELECT
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.CurrentSystemId'),
                    json_extract(value,'$.NewSystemId'),
                    json_extract(value,'$.ValidAfter'),
                    json_extract(value,'$.PreviousSystemId'),
                    json_extract(value,'$.CurrentSince')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(AllianceId) DO UPDATE SET
                    CurrentSystemId=excluded.CurrentSystemId,
                    NewSystemId=excluded.NewSystemId,
                    ValidAfter=excluded.ValidAfter,
                    PreviousSystemId=excluded.PreviousSystemId,
                    CurrentSince=excluded.CurrentSince
                """
            );
        }
    }
}
