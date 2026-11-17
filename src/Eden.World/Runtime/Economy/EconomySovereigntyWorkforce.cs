using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyWorkforce(long HubId, int SystemId, SovereigntyWorkforceMode Mode, int? Destination, long Amount);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyWorkforce() =>
        Register(
            new EconomyTable<SavedSovereigntyWorkforce, EconomySovereigntyWorkforce>(
                "WorldSovereigntyWorkforce",
                row => new(row.HubId),
                row => new(row.HubId, row.SystemId, row.Mode, row.Destination, row.Amount),
                state => new SavedSovereigntyWorkforce
                {
                    HubId = state.HubId,
                    SystemId = state.SystemId,
                    Mode = state.Mode,
                    Destination = state.Destination,
                    Amount = state.Amount,
                },
                ProjectSovereigntyWorkforce,
                "HubId,SystemId,Mode,Destination,Amount",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        (SovereigntyWorkforceMode)reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.GetInt64(5)
                    ),
                state => new(state.HubId),
                indexes: [new("SystemId", row => row.SystemId)],
                primaryColumn: "HubId"
            )
        );

    private static void ProjectSovereigntyWorkforce(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyWorkforce>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyWorkforce WHERE HubId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyWorkforce(HubId,SystemId,Mode,Destination,Amount)
                SELECT
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.Mode'),
                    json_extract(value,'$.Destination'),
                    json_extract(value,'$.Amount')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(HubId) DO UPDATE SET
                    SystemId=excluded.SystemId,
                    Mode=excluded.Mode,
                    Destination=excluded.Destination,
                    Amount=excluded.Amount
                """
            );
        }
    }
}
