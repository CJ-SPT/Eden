using System.Text.Json;
using Eden.Data.World.Flight;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record CynoDeployableRow(
        long Id,
        long BeaconId,
        long PilotId,
        long FleetId,
        int SystemId,
        int TypeId,
        long OnlineAt,
        long ExpiresAt,
        double X,
        double Y,
        double Z,
        double? Shield,
        double? Armor,
        double? Structure,
        long DamagedAt
    );

    private void RegisterCynoDeployables()
    {
        Register(
            new EconomyTable<SavedCynoDeployable, CynoDeployableRow>(
                "WorldCynoDeployable",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.BeaconId,
                        row.PilotId,
                        row.FleetId,
                        row.SystemId,
                        row.TypeId,
                        row.OnlineAt,
                        row.ExpiresAt,
                        row.X,
                        row.Y,
                        row.Z,
                        row.Shield,
                        row.Armor,
                        row.Structure,
                        row.DamagedAt
                    ),
                row =>
                    new()
                    {
                        Id = row.Id,
                        BeaconId = row.BeaconId,
                        PilotId = row.PilotId,
                        FleetId = row.FleetId,
                        SystemId = row.SystemId,
                        TypeId = row.TypeId,
                        OnlineAt = row.OnlineAt,
                        ExpiresAt = row.ExpiresAt,
                        X = row.X,
                        Y = row.Y,
                        Z = row.Z,
                        Shield = row.Shield,
                        Armor = row.Armor,
                        Structure = row.Structure,
                        DamagedAt = row.DamagedAt,
                    },
                ProjectCynoDeployables,
                "Id,BeaconId,PilotId,FleetId,SystemId,TypeId,OnlineAt,ExpiresAt,X,Y,Z,Shield,Armor,Structure,DamagedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetDouble(9),
                        reader.GetDouble(10),
                        reader.GetDouble(11),
                        reader.IsDBNull(12) ? null : reader.GetDouble(12),
                        reader.IsDBNull(13) ? null : reader.GetDouble(13),
                        reader.IsDBNull(14) ? null : reader.GetDouble(14),
                        reader.GetInt64(15)
                    ),
                row => new(row.Id),
                indexes:
                [
                    new(nameof(SavedCynoDeployable.SystemId), row => row.SystemId),
                    new(nameof(SavedCynoDeployable.FleetId), row => row.FleetId),
                ],
                changed: (_, row) => ObserveCynoDeadline(row?.ExpiresAt),
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectCynoDeployables(WorldDbContext world, IReadOnlyList<EconomyChange<CynoDeployableRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            string values = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<CynoDeployableRow>());
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCynoDeployable WHERE Id IN (SELECT value FROM json_each({removed}));
                INSERT INTO WorldCynoDeployable
                    (Id,BeaconId,PilotId,FleetId,SystemId,TypeId,OnlineAt,ExpiresAt,X,Y,Z,Shield,Armor,Structure,DamagedAt)
                SELECT value->>'Id',value->>'BeaconId',value->>'PilotId',value->>'FleetId',value->>'SystemId',value->>'TypeId',
                    value->>'OnlineAt',value->>'ExpiresAt',value->>'X',value->>'Y',value->>'Z',value->>'Shield',
                    value->>'Armor',value->>'Structure',value->>'DamagedAt'
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    Shield=excluded.Shield,Armor=excluded.Armor,Structure=excluded.Structure,DamagedAt=excluded.DamagedAt;
                """
            );
        }
    }
}
