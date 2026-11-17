using System.Text.Json;
using Eden.Data.World.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyStructureLoot(
    long Id,
    long StructureId,
    long OwnerId,
    int CorporationId,
    int SystemId,
    string Name,
    double X,
    double Y,
    double Z,
    long ExpiresAt
);

internal sealed partial class ResidentEconomy
{
    private void RegisterStructureLoot() =>
        Register(
            new EconomyTable<SavedStructureLoot, EconomyStructureLoot>(
                "WorldStructureLoot",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.StructureId,
                        row.OwnerId,
                        row.CorporationId,
                        row.SystemId,
                        row.Name,
                        row.X,
                        row.Y,
                        row.Z,
                        row.ExpiresAt
                    ),
                state => new SavedStructureLoot
                {
                    Id = state.Id,
                    StructureId = state.StructureId,
                    OwnerId = state.OwnerId,
                    CorporationId = state.CorporationId,
                    SystemId = state.SystemId,
                    Name = state.Name,
                    X = state.X,
                    Y = state.Y,
                    Z = state.Z,
                    ExpiresAt = state.ExpiresAt,
                },
                ProjectStructureLoot,
                "Id,StructureId,OwnerId,CorporationId,SystemId,Name,X,Y,Z,ExpiresAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetString(6),
                        reader.GetDouble(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9),
                        reader.GetInt64(10)
                    ),
                state => new(state.Id),
                indexes: [new("SystemId", row => row.SystemId), new("OwnerId", row => row.OwnerId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectStructureLoot(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyStructureLoot>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldStructureLoot WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldStructureLoot(Id,StructureId,OwnerId,CorporationId,SystemId,Name,X,Y,Z,ExpiresAt)
                SELECT
                    json_extract(value,'$.Id'),
                    json_extract(value,'$.StructureId'),
                    json_extract(value,'$.OwnerId'),
                    json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.Name'),
                    json_extract(value,'$.X'),
                    json_extract(value,'$.Y'),
                    json_extract(value,'$.Z'),
                    json_extract(value,'$.ExpiresAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    StructureId=excluded.StructureId,
                    OwnerId=excluded.OwnerId,
                    CorporationId=excluded.CorporationId,
                    SystemId=excluded.SystemId,
                    Name=excluded.Name,
                    X=excluded.X,
                    Y=excluded.Y,
                    Z=excluded.Z,
                    ExpiresAt=excluded.ExpiresAt
                """
            );
        }
    }
}
