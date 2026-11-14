using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyScanProbe(
    long Id,
    long PilotId,
    int SystemId,
    int TypeId,
    double X,
    double Y,
    double Z,
    long ExpiresAt,
    string ProfileJson
);

internal sealed record EconomyProbeScan(long PilotId, long StartedAt, long ReadyAt, string ScanJson);

internal sealed record EconomyResolvedSignature(long PilotId, long SiteId, int SystemId);

internal sealed partial class ResidentEconomy
{
    private void RegisterProbeScanning()
    {
        Register(
            new EconomyTable<SavedScanProbe, EconomyScanProbe>(
                "WorldScanProbe",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.SystemId, row.TypeId, row.X, row.Y, row.Z, row.ExpiresAt, row.ProfileJson),
                state => new SavedScanProbe
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    SystemId = state.SystemId,
                    TypeId = state.TypeId,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                    ExpiresAt = state.ExpiresAt,
                    ProfileJson = state.ProfileJson,
                },
                ProjectScanProbe,
                "Id,PilotId,SystemId,TypeId,X,Y,Z,ExpiresAt,ProfileJson",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetDouble(5),
                        reader.GetDouble(6),
                        reader.GetDouble(7),
                        reader.GetInt64(8),
                        reader.GetString(9)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedProbeScan, EconomyProbeScan>(
                "WorldProbeScan",
                row => new(row.PilotId),
                row => new(row.PilotId, row.StartedAt, row.ReadyAt, row.ScanJson),
                state => new SavedProbeScan
                {
                    PilotId = state.PilotId,
                    StartedAt = state.StartedAt,
                    ReadyAt = state.ReadyAt,
                    ScanJson = state.ScanJson,
                },
                ProjectProbeScan,
                "PilotId,StartedAt,ReadyAt,ScanJson",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)),
                state => new(state.PilotId),
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<SavedResolvedSignature, EconomyResolvedSignature>(
                "WorldResolvedSignature",
                row => new(row.PilotId, row.SiteId),
                row => new(row.PilotId, row.SiteId, row.SystemId),
                state => new SavedResolvedSignature
                {
                    PilotId = state.PilotId,
                    SiteId = state.SiteId,
                    SystemId = state.SystemId,
                },
                ProjectResolvedSignature,
                "PilotId,SiteId,SystemId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)),
                state => new(state.PilotId, state.SiteId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectScanProbe(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyScanProbe>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldScanProbe WHERE Id IN (SELECT value FROM json_each({removed}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldScanProbe(Id,PilotId,SystemId,TypeId,X,Y,Z,ExpiresAt,ProfileJson)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'), json_extract(value,'$.SystemId'),
                    json_extract(value,'$.TypeId'), json_extract(value,'$.X'), json_extract(value,'$.Y'),
                    json_extract(value,'$.Z'), json_extract(value,'$.ExpiresAt'), json_extract(value,'$.ProfileJson')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET X=excluded.X,Y=excluded.Y,Z=excluded.Z,
                    ProfileJson=excluded.ProfileJson
                """
            );
        }
    }

    private static void ProjectProbeScan(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyProbeScan>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldProbeScan WHERE PilotId IN (SELECT value FROM json_each({removed}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldProbeScan(PilotId,StartedAt,ReadyAt,ScanJson)
                SELECT json_extract(value,'$.PilotId'), json_extract(value,'$.StartedAt'), json_extract(value,'$.ReadyAt'),
                    json_extract(value,'$.ScanJson')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET StartedAt=excluded.StartedAt,ReadyAt=excluded.ReadyAt,ScanJson=excluded.ScanJson
                """
            );
        }
    }

    private static void ProjectResolvedSignature(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyResolvedSignature>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldResolvedSignature WHERE EXISTS (
                    SELECT 1 FROM json_each({removed})
                    WHERE PilotId=json_extract(value,'$.Id') AND SiteId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldResolvedSignature(PilotId,SiteId,SystemId)
                SELECT json_extract(value,'$.PilotId'), json_extract(value,'$.SiteId'), json_extract(value,'$.SystemId')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,SiteId) DO UPDATE SET SystemId=excluded.SystemId
                """
            );
        }
    }
}
