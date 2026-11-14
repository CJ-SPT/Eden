using System.Text.Json;
using Eden.Data.World.Loot;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record EconomyJetcan(
        long Id,
        long OwnerId,
        long ShipId,
        int CorporationId,
        int SystemId,
        double X,
        double Y,
        double Z,
        long ExpiresAt
    );

    private sealed record EconomyJettisonCooldown(long PilotId, long ReadyAt);

    private void RegisterJetcans()
    {
        Register(
            new EconomyTable<SavedJetcan, EconomyJetcan>(
                "WorldJetcan",
                row => new(row.Id),
                row => new(row.Id, row.OwnerId, row.ShipId, row.CorporationId, row.SystemId, row.X, row.Y, row.Z, row.ExpiresAt),
                row =>
                    new()
                    {
                        Id = row.Id,
                        OwnerId = row.OwnerId,
                        ShipId = row.ShipId,
                        CorporationId = row.CorporationId,
                        SystemId = row.SystemId,
                        X = row.X,
                        Y = row.Y,
                        Z = row.Z,
                        ExpiresAt = row.ExpiresAt,
                    },
                ProjectJetcans,
                "Id,OwnerId,ShipId,CorporationId,SystemId,X,Y,Z,ExpiresAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetDouble(6),
                        reader.GetDouble(7),
                        reader.GetDouble(8),
                        reader.GetInt64(9)
                    ),
                row => new(row.Id),
                indexes: [new(nameof(SavedJetcan.SystemId), row => row.SystemId), new(nameof(SavedJetcan.OwnerId), row => row.OwnerId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedJettisonCooldown, EconomyJettisonCooldown>(
                "WorldJettisonCooldown",
                row => new(row.PilotId),
                row => new(row.PilotId, row.ReadyAt),
                row => new() { PilotId = row.PilotId, ReadyAt = row.ReadyAt },
                ProjectJettisonCooldowns,
                "PilotId,ReadyAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                row => new(row.PilotId),
                primaryColumn: "PilotId"
            )
        );
    }

    private static void ProjectJetcans(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyJetcan>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key.Id).ToArray();
            if (removed.Length > 0)
            {
                world.Database.ExecuteSql(
                    $"DELETE FROM WorldJetcan WHERE Id IN (SELECT value FROM json_each({JsonSerializer.Serialize(removed)}))"
                );
            }
            var rows = batch.Select(change => change.Value).OfType<EconomyJetcan>().ToArray();
            if (rows.Length > 0)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldJetcan(Id,OwnerId,ShipId,CorporationId,SystemId,X,Y,Z,ExpiresAt)
                    SELECT json_extract(value,'$.Id'),json_extract(value,'$.OwnerId'),json_extract(value,'$.ShipId'),
                        json_extract(value,'$.CorporationId'),json_extract(value,'$.SystemId'),json_extract(value,'$.X'),
                        json_extract(value,'$.Y'),json_extract(value,'$.Z'),json_extract(value,'$.ExpiresAt')
                    FROM json_each({JsonSerializer.Serialize(rows)}) WHERE 1
                    ON CONFLICT(Id) DO NOTHING
                    """
                );
            }
        }
    }

    private static void ProjectJettisonCooldowns(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyJettisonCooldown>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key.Id).ToArray();
            if (removed.Length > 0)
            {
                world.Database.ExecuteSql(
                    $"DELETE FROM WorldJettisonCooldown WHERE PilotId IN (SELECT value FROM json_each({JsonSerializer.Serialize(removed)}))"
                );
            }
            var rows = batch.Select(change => change.Value).OfType<EconomyJettisonCooldown>().ToArray();
            if (rows.Length > 0)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldJettisonCooldown(PilotId,ReadyAt)
                    SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.ReadyAt')
                    FROM json_each({JsonSerializer.Serialize(rows)}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET ReadyAt=excluded.ReadyAt
                    """
                );
            }
        }
    }
}
