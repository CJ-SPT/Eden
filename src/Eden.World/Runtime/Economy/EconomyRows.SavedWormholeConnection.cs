using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWormholeConnection(
    long Id,
    long EndpointId,
    int SourceSystem,
    int DestinationSystem,
    int TypeId,
    int Generation,
    long ActivatedAt,
    long ExpiresAt,
    bool ExitVisible,
    double TotalMass,
    double RemainingMass,
    long MassUpdatedAt,
    long ReplaceAt
);

internal sealed partial class ResidentEconomy
{
    private void RegisterWormholeConnections() =>
        Register(
            new EconomyTable<SavedWormholeConnection, EconomyWormholeConnection>(
                "WorldWormholeConnection",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.EndpointId,
                        row.SourceSystem,
                        row.DestinationSystem,
                        row.TypeId,
                        row.Generation,
                        row.ActivatedAt,
                        row.ExpiresAt,
                        row.ExitVisible,
                        row.TotalMass,
                        row.RemainingMass,
                        row.MassUpdatedAt,
                        row.ReplaceAt
                    ),
                state => new SavedWormholeConnection
                {
                    Id = state.Id,
                    EndpointId = state.EndpointId,
                    SourceSystem = state.SourceSystem,
                    DestinationSystem = state.DestinationSystem,
                    TypeId = state.TypeId,
                    Generation = state.Generation,
                    ActivatedAt = state.ActivatedAt,
                    ExpiresAt = state.ExpiresAt,
                    ExitVisible = state.ExitVisible,
                    TotalMass = state.TotalMass,
                    RemainingMass = state.RemainingMass,
                    MassUpdatedAt = state.MassUpdatedAt,
                    ReplaceAt = state.ReplaceAt,
                },
                ProjectWormholeConnections,
                "Id,EndpointId,SourceSystem,DestinationSystem,TypeId,Generation,ActivatedAt,ExpiresAt,"
                    + "ExitVisible,TotalMass,RemainingMass,MassUpdatedAt,ReplaceAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetBoolean(9),
                        reader.GetDouble(10),
                        reader.GetDouble(11),
                        reader.GetInt64(12),
                        reader.GetInt64(13)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("EndpointId", row => row.EndpointId),
                    new("SourceSystem", row => row.SourceSystem),
                    new("DestinationSystem", row => row.DestinationSystem),
                ]
            )
        );

    private static void ProjectWormholeConnections(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWormholeConnection>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key.Id).ToArray();
            if (removed.Length > 0)
            {
                string ids = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql($"DELETE FROM WorldWormholeConnection WHERE Id IN (SELECT value FROM json_each({ids}))");
            }
            string values = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldWormholeConnection(
                    Id,EndpointId,SourceSystem,DestinationSystem,TypeId,Generation,ActivatedAt,ExpiresAt,
                    ExitVisible,TotalMass,RemainingMass,MassUpdatedAt,ReplaceAt
                )
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.EndpointId'),
                    json_extract(value,'$.SourceSystem'),
                    json_extract(value,'$.DestinationSystem'),
                    json_extract(value,'$.TypeId'),
                    json_extract(value,'$.Generation'),
                    json_extract(value,'$.ActivatedAt'),
                    json_extract(value,'$.ExpiresAt'),
                    json_extract(value,'$.ExitVisible'),
                    json_extract(value,'$.TotalMass'),
                    json_extract(value,'$.RemainingMass'),
                    json_extract(value,'$.MassUpdatedAt'),
                    json_extract(value,'$.ReplaceAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    EndpointId=excluded.EndpointId,
                    SourceSystem=excluded.SourceSystem,
                    DestinationSystem=excluded.DestinationSystem,
                    TypeId=excluded.TypeId,
                    Generation=excluded.Generation,
                    ActivatedAt=excluded.ActivatedAt,
                    ExpiresAt=excluded.ExpiresAt,
                    ExitVisible=excluded.ExitVisible,
                    TotalMass=excluded.TotalMass,
                    RemainingMass=excluded.RemainingMass,
                    MassUpdatedAt=excluded.MassUpdatedAt,
                    ReplaceAt=excluded.ReplaceAt
                """
            );
        }
    }
}
