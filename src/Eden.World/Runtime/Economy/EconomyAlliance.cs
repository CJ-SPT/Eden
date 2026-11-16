using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAlliance(
    int Id,
    string Name,
    string Ticker,
    string Description,
    string Url,
    long CreatorId,
    int CreatorCorporationId,
    int? ExecutorCorporationId,
    long CreatedAt,
    long? ClosedAt
);

internal sealed partial class ResidentEconomy
{
    private void RegisterAlliance() =>
        Register(
            new EconomyTable<SavedAlliance, EconomyAlliance>(
                "WorldAlliance",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.Name,
                        row.Ticker,
                        row.Description,
                        row.Url,
                        row.CreatorId,
                        row.CreatorCorporationId,
                        row.ExecutorCorporationId,
                        row.CreatedAt,
                        row.ClosedAt
                    ),
                state => new SavedAlliance
                {
                    Id = state.Id,
                    Name = state.Name,
                    Ticker = state.Ticker,
                    Description = state.Description,
                    Url = state.Url,
                    CreatorId = state.CreatorId,
                    CreatorCorporationId = state.CreatorCorporationId,
                    ExecutorCorporationId = state.ExecutorCorporationId,
                    CreatedAt = state.CreatedAt,
                    ClosedAt = state.ClosedAt,
                },
                ProjectAlliance,
                "Id,Name,Ticker,Description,Url,CreatorId,CreatorCorporationId,ExecutorCorporationId,CreatedAt,ClosedAt",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetInt64(6),
                        reader.GetInt32(7),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10)
                    ),
                state => new(state.Id),
                indexes: [new("Name", row => row.Name), new("Ticker", row => row.Ticker)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectAlliance(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAlliance>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAlliance WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAlliance(
                    Id,Name,Ticker,Description,Url,CreatorId,CreatorCorporationId,ExecutorCorporationId,CreatedAt,ClosedAt)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.Name'),
                    json_extract(value,'$.Ticker'),
                    json_extract(value,'$.Description'),
                    json_extract(value,'$.Url'),
                    json_extract(value,'$.CreatorId'),
                    json_extract(value,'$.CreatorCorporationId'),
                    json_extract(value,'$.ExecutorCorporationId'),
                    json_extract(value,'$.CreatedAt'),
                    json_extract(value,'$.ClosedAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    Name=excluded.Name,
                    Ticker=excluded.Ticker,
                    Description=excluded.Description,
                    Url=excluded.Url,
                    CreatorId=excluded.CreatorId,
                    CreatorCorporationId=excluded.CreatorCorporationId,
                    ExecutorCorporationId=excluded.ExecutorCorporationId,
                    CreatedAt=excluded.CreatedAt,
                    ClosedAt=excluded.ClosedAt
                """
            );
        }
    }
}
