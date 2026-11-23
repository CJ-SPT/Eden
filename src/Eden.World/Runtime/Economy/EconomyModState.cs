using System.Text.Json;
using Eden.Data.World.Modding;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record EconomyModState(string Id, string Json);

    private void RegisterModState() =>
        Register(
            new EconomyTable<SavedWorldModState, EconomyModState>(
                "WorldModState",
                row => new(0, Name: row.Id),
                row => new(row.Id, row.Json),
                state => new() { Id = state.Id, Json = state.Json },
                ProjectModState,
                "Id,Json",
                reader => new(reader.GetString(1), reader.GetString(2)),
                state => new(0, Name: state.Id),
                primaryColumn: null
            )
        );

    private static void ProjectModState(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyModState>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Name!));
            world.Database.ExecuteSql($"DELETE FROM WorldModState WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Select(change => change.Value).OfType<EconomyModState>().Chunk(ProjectionBatchSize))
        {
            string rows = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldModState(Id,Json)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.Json') FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET Json=excluded.Json
                """
            );
        }
    }
}
