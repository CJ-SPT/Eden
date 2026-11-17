using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyFuel(long Id, long HubId, int TypeId, long Amount, long Remainder);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyFuel() =>
        Register(
            new EconomyTable<SavedSovereigntyFuel, EconomySovereigntyFuel>(
                "WorldSovereigntyFuel",
                row => new(row.Id),
                row => new(row.Id, row.HubId, row.TypeId, row.Amount, row.Remainder),
                state => new SavedSovereigntyFuel
                {
                    Id = state.Id,
                    HubId = state.HubId,
                    TypeId = state.TypeId,
                    Amount = state.Amount,
                    Remainder = state.Remainder,
                },
                ProjectSovereigntyFuel,
                "Id,HubId,TypeId,Amount,Remainder",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5)),
                state => new(state.Id),
                indexes: [new("HubId", row => row.HubId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectSovereigntyFuel(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyFuel>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyFuel WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyFuel(Id,HubId,TypeId,Amount,Remainder)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.TypeId'),
                    json_extract(value,'$.Amount'),
                    json_extract(value,'$.Remainder')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    HubId=excluded.HubId,
                    TypeId=excluded.TypeId,
                    Amount=excluded.Amount,
                    Remainder=excluded.Remainder
                """
            );
        }
    }
}
