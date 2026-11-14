using System.Text.Json;
using Eden.Data.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedFitting(
    long Id,
    long OwnerId,
    int ShipTypeId,
    string Name,
    string Description,
    string ItemsJson,
    long SavedAt
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSavedFittings()
    {
        Register(
            new EconomyTable<SavedFittingRecord, EconomySavedFitting>(
                "WorldSavedFitting",
                row => new(row.Id),
                row => new(row.Id, row.OwnerId, row.ShipTypeId, row.Name, row.Description, row.ItemsJson, row.SavedAt),
                state => new SavedFittingRecord
                {
                    Id = state.Id,
                    OwnerId = state.OwnerId,
                    ShipTypeId = state.ShipTypeId,
                    Name = state.Name,
                    Description = state.Description,
                    ItemsJson = state.ItemsJson,
                    SavedAt = state.SavedAt,
                },
                ProjectSavedFittings,
                "Id,OwnerId,ShipTypeId,Name,Description,ItemsJson,SavedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetInt64(7)
                    ),
                state => new(state.Id),
                indexes: [new("OwnerId", row => row.OwnerId)],
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectSavedFittings(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedFitting>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSavedFitting WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSavedFitting(Id,OwnerId,ShipTypeId,Name,Description,ItemsJson,SavedAt)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.OwnerId'), json_extract(value,'$.ShipTypeId'),
                    json_extract(value,'$.Name'), json_extract(value,'$.Description'), json_extract(value,'$.ItemsJson'),
                    json_extract(value,'$.SavedAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET ShipTypeId=excluded.ShipTypeId, Name=excluded.Name, Description=excluded.Description,
                    ItemsJson=excluded.ItemsJson, SavedAt=excluded.SavedAt
                """
            );
        }
    }
}
