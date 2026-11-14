using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyExplorationSite(
    long Id,
    int SystemId,
    int Kind,
    int DungeonId,
    double X,
    double Y,
    double Z,
    long ExpiresAt,
    long? ReplaceAt,
    long? ResponseAt,
    bool ResponseSpawned,
    bool Detonated
);

internal sealed partial class ResidentEconomy
{
    private void RegisterExplorationSites()
    {
        Register(
            new EconomyTable<SavedExplorationSite, EconomyExplorationSite>(
                "WorldExplorationSite",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.SystemId,
                        row.Kind,
                        row.DungeonId,
                        row.X,
                        row.Y,
                        row.Z,
                        row.ExpiresAt,
                        row.ReplaceAt,
                        row.ResponseAt,
                        row.ResponseSpawned,
                        row.Detonated
                    ),
                state => new SavedExplorationSite
                {
                    Id = state.Id,
                    SystemId = state.SystemId,
                    Kind = state.Kind,
                    DungeonId = state.DungeonId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                    ExpiresAt = state.ExpiresAt,
                    ReplaceAt = state.ReplaceAt,
                    ResponseAt = state.ResponseAt,
                    ResponseSpawned = state.ResponseSpawned,
                    Detonated = state.Detonated,
                },
                ProjectExplorationSites,
                "Id,SystemId,Kind,DungeonId,X,Y,Z,ExpiresAt,ReplaceAt,ResponseAt,ResponseSpawned,Detonated",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetDouble(5),
                        reader.GetDouble(6),
                        reader.GetDouble(7),
                        reader.GetInt64(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        reader.GetBoolean(11),
                        reader.GetBoolean(12)
                    ),
                state => new(state.Id),
                indexes: [new("SystemId", row => row.SystemId)],
                primaryColumn: "Id"
            )
        );
    }

    private static void ProjectExplorationSites(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyExplorationSite>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldExplorationSite WHERE Id IN (SELECT value FROM json_each({deleted}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldExplorationSite(
                    Id,SystemId,Kind,DungeonId,X,Y,Z,ExpiresAt,ReplaceAt,ResponseAt,ResponseSpawned,Detonated)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.SystemId'),json_extract(value,'$.Kind'),
                    json_extract(value,'$.DungeonId'),json_extract(value,'$.X'),json_extract(value,'$.Y'),json_extract(value,'$.Z'),
                    json_extract(value,'$.ExpiresAt'),json_extract(value,'$.ReplaceAt'),json_extract(value,'$.ResponseAt'),
                    json_extract(value,'$.ResponseSpawned'),json_extract(value,'$.Detonated')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET ReplaceAt=excluded.ReplaceAt,ResponseAt=excluded.ResponseAt,
                    ResponseSpawned=excluded.ResponseSpawned,Detonated=excluded.Detonated
                """
            );
        }
    }
}
