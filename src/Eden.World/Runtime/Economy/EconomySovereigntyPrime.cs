using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyPrime(int AllianceId, int CurrentHour, int? NewHour, long? ValidAfter);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyPrime() =>
        Register(
            new EconomyTable<SavedSovereigntyPrime, EconomySovereigntyPrime>(
                "WorldSovereigntyPrime",
                row => new(row.AllianceId),
                row => new(row.AllianceId, row.CurrentHour, row.NewHour, row.ValidAfter),
                state => new SavedSovereigntyPrime
                {
                    AllianceId = state.AllianceId,
                    CurrentHour = state.CurrentHour,
                    NewHour = state.NewHour,
                    ValidAfter = state.ValidAfter,
                },
                ProjectSovereigntyPrime,
                "AllianceId,CurrentHour,NewHour,ValidAfter",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4)
                    ),
                state => new(state.AllianceId),
                indexes: [],
                primaryColumn: "AllianceId"
            )
        );

    private static void ProjectSovereigntyPrime(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyPrime>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyPrime WHERE AllianceId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyPrime(
                    AllianceId,
                    CurrentHour,
                    NewHour,
                    ValidAfter)
                SELECT
                    json_extract(value,'$.AllianceId'),
                    json_extract(value,'$.CurrentHour'),
                    json_extract(value,'$.NewHour'),
                    json_extract(value,'$.ValidAfter')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(AllianceId) DO UPDATE SET
                    CurrentHour=excluded.CurrentHour,
                    NewHour=excluded.NewHour,
                    ValidAfter=excluded.ValidAfter
                """
            );
        }
    }
}
