using System.Text.Json;
using Eden.Data.World.Flight;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private long _latestCynoDeadline;

    internal bool MayHaveCynos(long now) => !IsLoaded || Volatile.Read(ref _latestCynoDeadline) > now;

    private sealed record TimerRow(long PilotId, long ActivationUntil, long FatigueUntil, long UpdatedAt);

    private sealed record FieldRow(
        long PilotId,
        long ShipId,
        long ModuleId,
        long BeaconId,
        long FleetId,
        int SystemId,
        long EndsAt,
        double X,
        double Y,
        double Z,
        float Radius,
        double SpawnRadius,
        int TypeId,
        long StartedAt
    );

    private void RegisterCapitalTravel()
    {
        Register(
            new EconomyTable<SavedJumpTimers, TimerRow>(
                "WorldJumpTimers",
                row => new(row.PilotId),
                row => new(row.PilotId, row.ActivationUntil, row.FatigueUntil, row.UpdatedAt),
                row =>
                    new()
                    {
                        PilotId = row.PilotId,
                        ActivationUntil = row.ActivationUntil,
                        FatigueUntil = row.FatigueUntil,
                        UpdatedAt = row.UpdatedAt,
                    },
                ProjectJumpTimers,
                "PilotId,ActivationUntil,FatigueUntil,UpdatedAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
                row => new(row.PilotId),
                primaryColumn: "PilotId",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedCynosuralField, FieldRow>(
                "WorldCynosuralField",
                row => new(row.PilotId),
                row =>
                    new(
                        row.PilotId,
                        row.ShipId,
                        row.ModuleId,
                        row.BeaconId,
                        row.FleetId,
                        row.SystemId,
                        row.EndsAt,
                        row.X,
                        row.Y,
                        row.Z,
                        row.Radius,
                        row.SpawnRadius,
                        row.TypeId,
                        row.StartedAt
                    ),
                row =>
                    new()
                    {
                        PilotId = row.PilotId,
                        ShipId = row.ShipId,
                        ModuleId = row.ModuleId,
                        BeaconId = row.BeaconId,
                        FleetId = row.FleetId,
                        SystemId = row.SystemId,
                        EndsAt = row.EndsAt,
                        X = row.X,
                        Y = row.Y,
                        Z = row.Z,
                        Radius = row.Radius,
                        SpawnRadius = row.SpawnRadius,
                        TypeId = row.TypeId,
                        StartedAt = row.StartedAt,
                    },
                ProjectCynosuralFields,
                "PilotId,ShipId,ModuleId,BeaconId,FleetId,SystemId,EndsAt,X,Y,Z,Radius,SpawnRadius,TypeId,StartedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetDouble(8),
                        reader.GetDouble(9),
                        reader.GetDouble(10),
                        reader.GetFloat(11),
                        reader.GetDouble(12),
                        reader.GetInt32(13),
                        reader.GetInt64(14)
                    ),
                row => new(row.PilotId),
                indexes:
                [
                    new(nameof(SavedCynosuralField.SystemId), row => row.SystemId),
                    new(nameof(SavedCynosuralField.FleetId), row => row.FleetId),
                ],
                changed: (_, row) =>
                {
                    if (row is not null && row.EndsAt > _latestCynoDeadline)
                    {
                        Volatile.Write(ref _latestCynoDeadline, row.EndsAt);
                    }
                },
                primaryColumn: "PilotId",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectJumpTimers(WorldDbContext world, IReadOnlyList<EconomyChange<TimerRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            string values = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<TimerRow>());
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldJumpTimers WHERE PilotId IN (SELECT value FROM json_each({deleted}));
                INSERT INTO WorldJumpTimers(PilotId,ActivationUntil,FatigueUntil,UpdatedAt)
                SELECT value->>'PilotId',value->>'ActivationUntil',value->>'FatigueUntil',value->>'UpdatedAt'
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET ActivationUntil=excluded.ActivationUntil,
                    FatigueUntil=excluded.FatigueUntil,UpdatedAt=excluded.UpdatedAt;
                """
            );
        }
    }

    private static void ProjectCynosuralFields(WorldDbContext world, IReadOnlyList<EconomyChange<FieldRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            string values = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<FieldRow>());
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldCynosuralField WHERE PilotId IN (SELECT value FROM json_each({deleted}));
                INSERT INTO WorldCynosuralField(
                    PilotId,ShipId,ModuleId,BeaconId,FleetId,SystemId,EndsAt,X,Y,Z,Radius,SpawnRadius,TypeId,StartedAt)
                SELECT value->>'PilotId',value->>'ShipId',value->>'ModuleId',value->>'BeaconId',value->>'FleetId',
                    value->>'SystemId',value->>'EndsAt',value->>'X',value->>'Y',value->>'Z',value->>'Radius',value->>'SpawnRadius',
                    value->>'TypeId',value->>'StartedAt'
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET ShipId=excluded.ShipId,ModuleId=excluded.ModuleId,
                    BeaconId=excluded.BeaconId,FleetId=excluded.FleetId,SystemId=excluded.SystemId,EndsAt=excluded.EndsAt,
                    X=excluded.X,Y=excluded.Y,Z=excluded.Z,Radius=excluded.Radius,SpawnRadius=excluded.SpawnRadius,
                    TypeId=excluded.TypeId,StartedAt=excluded.StartedAt;
                """
            );
        }
    }
}
