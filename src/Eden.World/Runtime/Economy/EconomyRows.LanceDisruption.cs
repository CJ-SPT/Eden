using Eden.Data.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record LanceDisruptionRow(long ShipId, long EndsAt);

    private void RegisterLanceDisruptions() =>
        Register(
            new EconomyTable<SavedLanceDisruption, LanceDisruptionRow>(
                "SavedLanceDisruption",
                row => new(row.ShipId),
                row => new(row.ShipId, row.EndsAt),
                row => new SavedLanceDisruption { ShipId = row.ShipId, EndsAt = row.EndsAt },
                ProjectLanceDisruptions,
                "ShipId,EndsAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                row => new(row.ShipId),
                primaryColumn: "ShipId",
                rowIdIsKey: true
            )
        );

    private static void ProjectLanceDisruptions(WorldDbContext world, IReadOnlyList<EconomyChange<LanceDisruptionRow>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is not { } row)
            {
                world.Database.ExecuteSql($"DELETE FROM SavedLanceDisruption WHERE ShipId={change.Key.Id}");
                continue;
            }
            world.Database.ExecuteSql(
                $"""
                INSERT INTO SavedLanceDisruption(ShipId,EndsAt) VALUES({row.ShipId},{row.EndsAt})
                ON CONFLICT(ShipId) DO UPDATE SET EndsAt=excluded.EndsAt
                """
            );
        }
    }
}
