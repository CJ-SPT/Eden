using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyKillRight(
    long Id,
    long OwnerId,
    long TargetId,
    long ExpiresAt,
    long? PriceCents,
    long? RestrictedTo,
    long ActiveUntil
);

internal sealed partial class ResidentEconomy
{
    private void RegisterKillRights() =>
        Register(
            new EconomyTable<SavedKillRight, EconomyKillRight>(
                "WorldKillRight",
                row => new(row.Id),
                row => new(row.Id, row.OwnerId, row.TargetId, row.ExpiresAt, row.PriceCents, row.RestrictedTo, row.ActiveUntil),
                row => new SavedKillRight
                {
                    Id = row.Id,
                    OwnerId = row.OwnerId,
                    TargetId = row.TargetId,
                    ExpiresAt = row.ExpiresAt,
                    PriceCents = row.PriceCents,
                    RestrictedTo = row.RestrictedTo,
                    ActiveUntil = row.ActiveUntil,
                },
                ProjectKillRights,
                "Id,OwnerId,TargetId,ExpiresAt,PriceCents,RestrictedTo,ActiveUntil",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.GetInt64(7)
                    ),
                row => new(row.Id),
                indexes: [new("OwnerId", row => row.OwnerId), new("TargetId", row => row.TargetId)],
                rowIdIsKey: true
            )
        );

    private static void ProjectKillRights(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyKillRight>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldKillRight WHERE Id IN (SELECT value FROM json_each({removed}))");
        }
        // Delete expired predecessor IDs before inserting replacements for the same pair.
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldKillRight(Id,OwnerId,TargetId,ExpiresAt,PriceCents,RestrictedTo,ActiveUntil)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.OwnerId'), json_extract(value,'$.TargetId'),
                    json_extract(value,'$.ExpiresAt'), json_extract(value,'$.PriceCents'), json_extract(value,'$.RestrictedTo'),
                    json_extract(value,'$.ActiveUntil')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET PriceCents=excluded.PriceCents, RestrictedTo=excluded.RestrictedTo,
                    ActiveUntil=excluded.ActiveUntil
                """
            );
        }
    }
}
