using System.Text.Json;
using Eden.Data.World.Planetary;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedPlanetaryColony(long PilotId, int PlanetId, string StateJson, string LastEditHash, long LastEditAt);

internal sealed record EconomySavedPlanetaryIdentity(int Id, long NextPinId);

internal sealed record EconomySavedPlanetaryLaunch(
    int Id,
    long PilotId,
    int PlanetId,
    int SystemId,
    long CreatedAt,
    long ExpiresAt,
    double X,
    double Y,
    double Z
);

internal sealed record EconomySavedPlanetaryOffice(int PlanetId, int SystemId, double X, double Y, double Z);

internal sealed partial class ResidentEconomy
{
    private void RegisterPlanetaryTables()
    {
        Register(
            new EconomyTable<SavedPlanetaryColony, EconomySavedPlanetaryColony>(
                "WorldPlanetaryColony",
                row => new(row.PilotId, row.PlanetId),
                row => new(row.PilotId, row.PlanetId, row.StateJson, row.LastEditHash, row.LastEditAt),
                state => new SavedPlanetaryColony
                {
                    PilotId = state.PilotId,
                    PlanetId = state.PlanetId,
                    StateJson = state.StateJson,
                    LastEditHash = state.LastEditHash,
                    LastEditAt = state.LastEditAt,
                },
                ProjectSavedPlanetaryColony,
                "PilotId,PlanetId,StateJson,LastEditHash,LastEditAt",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5)),
                state => new(state.PilotId, state.PlanetId),
                indexes: [new("PilotId", row => row.PilotId), new("PlanetId", row => row.PlanetId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedPlanetaryIdentity, EconomySavedPlanetaryIdentity>(
                "WorldPlanetaryIdentity",
                row => new(row.Id),
                row => new(row.Id, row.NextPinId),
                state => new SavedPlanetaryIdentity { Id = state.Id, NextPinId = state.NextPinId },
                ProjectSavedPlanetaryIdentity,
                "Id,NextPinId",
                reader => new(reader.GetInt32(1), reader.GetInt64(2)),
                state => new(state.Id),
                indexes: [],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedPlanetaryLaunch, EconomySavedPlanetaryLaunch>(
                "WorldPlanetaryLaunch",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.PlanetId, row.SystemId, row.CreatedAt, row.ExpiresAt, row.X, row.Y, row.Z),
                state => new SavedPlanetaryLaunch
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    PlanetId = state.PlanetId,
                    SystemId = state.SystemId,
                    CreatedAt = state.CreatedAt,
                    ExpiresAt = state.ExpiresAt,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectSavedPlanetaryLaunch,
                "Id,PilotId,PlanetId,SystemId,CreatedAt,ExpiresAt,X,Y," + "Z",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetDouble(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId), new("SystemId", row => row.SystemId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedPlanetaryOffice, EconomySavedPlanetaryOffice>(
                "WorldPlanetaryOffice",
                row => new(row.PlanetId),
                row => new(row.PlanetId, row.SystemId, row.X, row.Y, row.Z),
                state => new SavedPlanetaryOffice
                {
                    PlanetId = state.PlanetId,
                    SystemId = state.SystemId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                },
                ProjectSavedPlanetaryOffice,
                "PlanetId,SystemId,X,Y,Z",
                reader => new(reader.GetInt32(1), reader.GetInt32(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5)),
                state => new(state.PlanetId),
                indexes: [new("SystemId", row => row.SystemId)],
                primaryColumn: "PlanetId",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectSavedPlanetaryColony(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedPlanetaryColony>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldPlanetaryColony
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldPlanetaryColony.PilotId=json_extract(value,'$.Id') AND
                            WorldPlanetaryColony.PlanetId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPlanetaryColony(
                        PilotId,PlanetId,StateJson,LastEditHash,LastEditAt
                )
                SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.PlanetId'),json_extract(value,'$.StateJson'),
                        json_extract(value,'$.LastEditHash'),json_extract(value,'$.LastEditAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,PlanetId) DO UPDATE SET
                        StateJson=excluded.StateJson,LastEditHash=excluded.LastEditHash,LastEditAt=excluded.LastEditAt
                """
            );
        }
    }

    private static void ProjectSavedPlanetaryIdentity(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedPlanetaryIdentity>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldPlanetaryIdentity
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldPlanetaryIdentity.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPlanetaryIdentity(
                        Id,NextPinId
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.NextPinId')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        NextPinId=excluded.NextPinId
                """
            );
        }
    }

    private static void ProjectSavedPlanetaryLaunch(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedPlanetaryLaunch>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldPlanetaryLaunch
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldPlanetaryLaunch.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPlanetaryLaunch(
                        Id,PilotId,PlanetId,SystemId,CreatedAt,ExpiresAt,X,
                        Y,Z
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.PlanetId'),
                        json_extract(value,'$.SystemId'),json_extract(value,'$.CreatedAt'),json_extract(value,'$.ExpiresAt'),
                        json_extract(value,'$.X'),json_extract(value,'$.Y'),json_extract(value,'$.Z')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        PilotId=excluded.PilotId,PlanetId=excluded.PlanetId,SystemId=excluded.SystemId,
                        CreatedAt=excluded.CreatedAt,ExpiresAt=excluded.ExpiresAt,X=excluded.X,
                        Y=excluded.Y,Z=excluded.Z
                """
            );
        }
    }

    private static void ProjectSavedPlanetaryOffice(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedPlanetaryOffice>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldPlanetaryOffice
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldPlanetaryOffice.PlanetId=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPlanetaryOffice(
                        PlanetId,SystemId,X,Y,Z
                )
                SELECT
                        json_extract(value,'$.PlanetId'),json_extract(value,'$.SystemId'),json_extract(value,'$.X'),
                        json_extract(value,'$.Y'),json_extract(value,'$.Z')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PlanetId) DO UPDATE SET
                        SystemId=excluded.SystemId,X=excluded.X,Y=excluded.Y,
                        Z=excluded.Z
                """
            );
        }
    }
}
