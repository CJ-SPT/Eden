using System.Text.Json;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Propulsion;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record InterdictionRow(
        long Id,
        long PilotId,
        long SourceShipId,
        long ModuleId,
        int TypeId,
        int SystemId,
        InterdictionKind Kind,
        double X,
        double Y,
        double Z,
        double Radius,
        double HitPoints,
        long StartedAt,
        long ExpiresAt
    );

    private void RegisterInterdiction()
    {
        Register(
            new EconomyTable<SavedInterdictionField, InterdictionRow>(
                "SavedInterdictionField",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.PilotId,
                        row.SourceShipId,
                        row.ModuleId,
                        row.TypeId,
                        row.SystemId,
                        row.Kind,
                        row.X,
                        row.Y,
                        row.Z,
                        row.Radius,
                        row.HitPoints,
                        row.StartedAt,
                        row.ExpiresAt
                    ),
                row => new SavedInterdictionField
                {
                    Id = row.Id,
                    PilotId = row.PilotId,
                    SourceShipId = row.SourceShipId,
                    ModuleId = row.ModuleId,
                    TypeId = row.TypeId,
                    SystemId = row.SystemId,
                    Kind = row.Kind,
                    X = row.X,
                    Y = row.Y,
                    Z = row.Z,
                    Radius = row.Radius,
                    HitPoints = row.HitPoints,
                    StartedAt = row.StartedAt,
                    ExpiresAt = row.ExpiresAt,
                },
                ProjectInterdiction,
                "Id,PilotId,SourceShipId,ModuleId,TypeId,SystemId,Kind,X,Y,Z,Radius,HitPoints,StartedAt,ExpiresAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        (InterdictionKind)reader.GetInt32(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9),
                        reader.GetDouble(10),
                        reader.GetDouble(11),
                        reader.GetDouble(12),
                        reader.GetInt64(13),
                        reader.GetInt64(14)
                    ),
                row => new(row.Id),
                indexes: [new("SystemId", row => row.SystemId), new("ModuleId", row => row.ModuleId)],
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectInterdiction(WorldDbContext world, IReadOnlyList<EconomyChange<InterdictionRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(row => row.Value is null).Select(row => row.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM SavedInterdictionField WHERE Id IN (SELECT value FROM json_each({removed}))");
            string values = JsonSerializer.Serialize(batch.Where(row => row.Value is not null).Select(row => row.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO SavedInterdictionField(
                    Id,PilotId,SourceShipId,ModuleId,TypeId,SystemId,Kind,X,Y,Z,Radius,HitPoints,StartedAt,ExpiresAt
                )
                SELECT json_extract(value,'$.Id'),
                    json_extract(value,'$.PilotId'),
                    json_extract(value,'$.SourceShipId'),
                    json_extract(value,'$.ModuleId'),
                    json_extract(value,'$.TypeId'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.Kind'),
                    json_extract(value,'$.X'),
                    json_extract(value,'$.Y'),
                    json_extract(value,'$.Z'),
                    json_extract(value,'$.Radius'),
                    json_extract(value,'$.HitPoints'),
                    json_extract(value,'$.StartedAt'),
                    json_extract(value,'$.ExpiresAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    PilotId=excluded.PilotId,
                    SourceShipId=excluded.SourceShipId,
                    ModuleId=excluded.ModuleId,
                    TypeId=excluded.TypeId,
                    SystemId=excluded.SystemId,
                    Kind=excluded.Kind,
                    X=excluded.X,
                    Y=excluded.Y,
                    Z=excluded.Z,
                    Radius=excluded.Radius,
                    HitPoints=excluded.HitPoints,
                    StartedAt=excluded.StartedAt,
                    ExpiresAt=excluded.ExpiresAt
                """
            );
        }
    }
}
