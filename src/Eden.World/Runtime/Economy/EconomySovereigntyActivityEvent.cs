using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyActivityEvent(long Id, int SystemId, long At, SovereigntyActivityKind Kind, double Amount);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyActivityEvent() =>
        Register(
            new EconomyTable<SavedSovereigntyActivityEvent, EconomySovereigntyActivityEvent>(
                "WorldSovereigntyActivityEvent",
                row => new(row.Id),
                row => new(row.Id, row.SystemId, row.At, row.Kind, row.Amount),
                state => new SavedSovereigntyActivityEvent
                {
                    Id = state.Id,
                    SystemId = state.SystemId,
                    At = state.At,
                    Kind = state.Kind,
                    Amount = state.Amount,
                },
                ProjectSovereigntyActivityEvent,
                "Id,SystemId,At,Kind,Amount",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        (SovereigntyActivityKind)reader.GetInt32(4),
                        reader.GetDouble(5)
                    ),
                state => new(state.Id),
                indexes: [new("SystemId", row => row.SystemId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectSovereigntyActivityEvent(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySovereigntyActivityEvent>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyActivityEvent WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyActivityEvent(
                    Id,
                    SystemId,
                    At,
                    Kind,
                    Amount)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.At'),
                    json_extract(value,'$.Kind'),
                    json_extract(value,'$.Amount')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    SystemId=excluded.SystemId,
                    At=excluded.At,
                    Kind=excluded.Kind,
                    Amount=excluded.Amount
                """
            );
        }
    }
}
