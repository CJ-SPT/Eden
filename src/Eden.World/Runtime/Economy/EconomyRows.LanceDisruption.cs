using Eden.Data.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record LanceDisruptionRow(long ShipId, long EndsAt, long OscillatorEndsAt);

    private void RegisterLanceDisruptions() =>
        Register(
            new EconomyTable<SavedLanceDisruption, LanceDisruptionRow>(
                "SavedLanceDisruption",
                row => new(row.ShipId),
                row => new(row.ShipId, row.EndsAt, row.OscillatorEndsAt),
                row => new SavedLanceDisruption
                {
                    ShipId = row.ShipId,
                    EndsAt = row.EndsAt,
                    OscillatorEndsAt = row.OscillatorEndsAt,
                },
                ProjectLanceDisruptions,
                "ShipId,EndsAt,OscillatorEndsAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
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
                INSERT INTO SavedLanceDisruption(ShipId,EndsAt,OscillatorEndsAt) VALUES({row.ShipId},{row.EndsAt},{row.OscillatorEndsAt})
                ON CONFLICT(ShipId) DO UPDATE SET EndsAt=excluded.EndsAt,OscillatorEndsAt=excluded.OscillatorEndsAt
                """
            );
        }
    }
}
