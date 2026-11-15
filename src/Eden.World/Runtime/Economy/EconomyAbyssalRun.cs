using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAbyssalRun(
    long Id,
    long OwnerPilotId,
    int SystemId,
    int OriginSystemId,
    double OriginX,
    double OriginY,
    double OriginZ,
    int FilamentTypeId,
    int Mode,
    int Tier,
    int Weather,
    int Room,
    long CreatedAt,
    long ExpiresAt,
    int Status,
    int Seed,
    long EnvironmentAt,
    int DisabledFields
);

internal sealed partial class ResidentEconomy
{
    private void RegisterAbyssalRun() =>
        Register(
            new EconomyTable<SavedAbyssalRun, EconomyAbyssalRun>(
                "WorldAbyssalRun",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.OwnerPilotId,
                        row.SystemId,
                        row.OriginSystemId,
                        row.OriginX,
                        row.OriginY,
                        row.OriginZ,
                        row.FilamentTypeId,
                        row.Mode,
                        row.Tier,
                        row.Weather,
                        row.Room,
                        row.CreatedAt,
                        row.ExpiresAt,
                        row.Status,
                        row.Seed,
                        row.EnvironmentAt,
                        row.DisabledFields
                    ),
                state => new SavedAbyssalRun
                {
                    Id = state.Id,
                    OwnerPilotId = state.OwnerPilotId,
                    SystemId = state.SystemId,
                    OriginSystemId = state.OriginSystemId,
                    OriginX = state.OriginX,
                    OriginY = state.OriginY,
                    OriginZ = state.OriginZ,
                    FilamentTypeId = state.FilamentTypeId,
                    Mode = state.Mode,
                    Tier = state.Tier,
                    Weather = state.Weather,
                    Room = state.Room,
                    CreatedAt = state.CreatedAt,
                    ExpiresAt = state.ExpiresAt,
                    Status = state.Status,
                    Seed = state.Seed,
                    EnvironmentAt = state.EnvironmentAt,
                    DisabledFields = state.DisabledFields,
                },
                ProjectAbyssalRun,
                "Id,OwnerPilotId,SystemId,OriginSystemId,OriginX,OriginY,OriginZ,FilamentTypeId,"
                    + "Mode,Tier,Weather,Room,CreatedAt,ExpiresAt,Status,Seed,EnvironmentAt,DisabledFields",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetDouble(5),
                        reader.GetDouble(6),
                        reader.GetDouble(7),
                        reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetInt32(10),
                        reader.GetInt32(11),
                        reader.GetInt32(12),
                        reader.GetInt64(13),
                        reader.GetInt64(14),
                        reader.GetInt32(15),
                        reader.GetInt32(16),
                        reader.GetInt64(17),
                        reader.GetInt32(18)
                    ),
                state => new(state.Id),
                indexes:
                [
                    new("Id", row => row.Id),
                    new("OwnerPilotId", row => row.OwnerPilotId),
                    new("SystemId", row => row.SystemId),
                    new("OriginSystemId", row => row.OriginSystemId),
                    new("FilamentTypeId", row => row.FilamentTypeId),
                    new("Status", row => row.Status),
                ],
                primaryColumn: "Id"
            )
        );

    private static void ProjectAbyssalRun(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAbyssalRun>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAbyssalRun WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAbyssalRun(
                    Id,OwnerPilotId,SystemId,OriginSystemId,OriginX,OriginY,OriginZ,FilamentTypeId,
                    Mode,Tier,Weather,Room,CreatedAt,ExpiresAt,Status,Seed,EnvironmentAt,DisabledFields)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.OwnerPilotId'),json_extract(value,'$.SystemId'),
                    json_extract(value,'$.OriginSystemId'),json_extract(value,'$.OriginX'),json_extract(value,'$.OriginY'),
                    json_extract(value,'$.OriginZ'),json_extract(value,'$.FilamentTypeId'),json_extract(value,'$.Mode'),
                    json_extract(value,'$.Tier'),json_extract(value,'$.Weather'),json_extract(value,'$.Room'),
                    json_extract(value,'$.CreatedAt'),json_extract(value,'$.ExpiresAt'),json_extract(value,'$.Status'),
                    json_extract(value,'$.Seed'),json_extract(value,'$.EnvironmentAt'),json_extract(value,'$.DisabledFields')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET OwnerPilotId=excluded.OwnerPilotId,SystemId=excluded.SystemId,
                    OriginSystemId=excluded.OriginSystemId,OriginX=excluded.OriginX,OriginY=excluded.OriginY,OriginZ=excluded.OriginZ,
                    FilamentTypeId=excluded.FilamentTypeId,Mode=excluded.Mode,Tier=excluded.Tier,Weather=excluded.Weather,
                    Room=excluded.Room,CreatedAt=excluded.CreatedAt,ExpiresAt=excluded.ExpiresAt,Status=excluded.Status,
                    Seed=excluded.Seed,EnvironmentAt=excluded.EnvironmentAt,DisabledFields=excluded.DisabledFields
                """
            );
        }
    }
}
