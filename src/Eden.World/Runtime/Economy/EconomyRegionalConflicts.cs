using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyRegionalConflict(int RegionId, RegionalConflictRegion State);

internal sealed partial class ResidentEconomy
{
    private void RegisterRegionalConflicts() =>
        Register(
            new EconomyTable<SavedRegionalConflict, EconomyRegionalConflict>(
                "WorldRegionalConflict",
                row => new(row.RegionId),
                row => new(row.RegionId, row.State),
                state => new SavedRegionalConflict { RegionId = state.RegionId, State = state.State },
                ProjectRegionalConflicts,
                "RegionId,State",
                reader => new(reader.GetInt32(1), RegionalConflictSchema.Deserialize(reader.GetString(2))),
                state => new(state.RegionId),
                indexes: [new("RegionId", row => row.RegionId)],
                primaryColumn: "RegionId"
            )
        );

    private static void ProjectRegionalConflicts(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyRegionalConflict>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key).ToArray());
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldRegionalConflict WHERE RegionId IN (SELECT json_extract(value,'$.Id') FROM json_each({removed}));
                """
            );
            string values = JsonSerializer.Serialize(
                batch.Where(change => change.Value is not null).Select(change => change.Value).ToArray()
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldRegionalConflict(RegionId,State)
                SELECT json_extract(value,'$.RegionId'),json_extract(value,'$.State') FROM json_each({values}) WHERE 1
                ON CONFLICT(RegionId) DO UPDATE SET State=excluded.State;
                """
            );
        }
    }
}
