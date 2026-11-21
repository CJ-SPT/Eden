using System.Text.Json;
using Eden.Data.World.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMilitiaAdvantage(
    int SystemId,
    int FirstContribution,
    long FirstUpdatedAt,
    int SecondContribution,
    long SecondUpdatedAt
);

internal sealed partial class ResidentEconomy
{
    private void RegisterMilitiaAdvantage() =>
        Register(
            new EconomyTable<SavedMilitiaAdvantage, EconomyMilitiaAdvantage>(
                "WorldMilitiaAdvantage",
                row => new(row.SystemId),
                row => new(row.SystemId, row.FirstContribution, row.FirstUpdatedAt, row.SecondContribution, row.SecondUpdatedAt),
                state =>
                    new()
                    {
                        SystemId = state.SystemId,
                        FirstContribution = state.FirstContribution,
                        FirstUpdatedAt = state.FirstUpdatedAt,
                        SecondContribution = state.SecondContribution,
                        SecondUpdatedAt = state.SecondUpdatedAt,
                    },
                ProjectMilitiaAdvantage,
                "SystemId,FirstContribution,FirstUpdatedAt,SecondContribution,SecondUpdatedAt",
                reader => new(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetInt64(5)),
                state => new(state.SystemId),
                primaryColumn: "SystemId"
            )
        );

    private static void ProjectMilitiaAdvantage(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaAdvantage>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMilitiaAdvantage WHERE SystemId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaAdvantage(SystemId,FirstContribution,FirstUpdatedAt,SecondContribution,SecondUpdatedAt)
                SELECT json_extract(value,'$.SystemId'), json_extract(value,'$.FirstContribution'), json_extract(value,'$.FirstUpdatedAt'),
                    json_extract(value,'$.SecondContribution'), json_extract(value,'$.SecondUpdatedAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SystemId) DO UPDATE SET FirstContribution=excluded.FirstContribution, FirstUpdatedAt=excluded.FirstUpdatedAt,
                    SecondContribution=excluded.SecondContribution, SecondUpdatedAt=excluded.SecondUpdatedAt
                """
            );
        }
    }
}
