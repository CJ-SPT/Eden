using System.Text.Json;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMilitiaComplex(
    long Slot,
    int Generation,
    MilitiaFaction Occupier,
    long Progress,
    long? ReplaceAt,
    long? DefenderId
);

internal sealed partial class ResidentEconomy
{
    private void RegisterMilitiaComplex() =>
        Register(
            new EconomyTable<SavedMilitiaComplex, EconomyMilitiaComplex>(
                "WorldMilitiaComplex",
                row => new(row.Slot),
                row => new(row.Slot, row.Generation, row.Occupier, row.Progress, row.ReplaceAt, row.DefenderId),
                state =>
                    new()
                    {
                        Slot = state.Slot,
                        Generation = state.Generation,
                        Occupier = state.Occupier,
                        Progress = state.Progress,
                        ReplaceAt = state.ReplaceAt,
                        DefenderId = state.DefenderId,
                    },
                ProjectMilitiaComplex,
                "Slot,Generation,Occupier,Progress,ReplaceAt,DefenderId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        (MilitiaFaction)reader.GetInt32(3),
                        reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6)
                    ),
                state => new(state.Slot),
                primaryColumn: "Slot"
            )
        );

    private static void ProjectMilitiaComplex(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaComplex>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMilitiaComplex WHERE Slot IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaComplex(Slot,Generation,Occupier,Progress,ReplaceAt,DefenderId)
                SELECT json_extract(value,'$.Slot'), json_extract(value,'$.Generation'), json_extract(value,'$.Occupier'),
                    json_extract(value,'$.Progress'), json_extract(value,'$.ReplaceAt'), json_extract(value,'$.DefenderId')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Slot) DO UPDATE SET Generation=excluded.Generation, Occupier=excluded.Occupier,
                    Progress=excluded.Progress, ReplaceAt=excluded.ReplaceAt, DefenderId=excluded.DefenderId
                """
            );
        }
    }
}
