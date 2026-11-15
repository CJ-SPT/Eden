using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMutatedItem(long ItemId, string Payload);

internal sealed partial class ResidentEconomy
{
    private void RegisterMutatedItem() =>
        Register(
            new EconomyTable<SavedMutatedItem, EconomyMutatedItem>(
                "WorldMutatedItem",
                row => new(row.ItemId),
                row => new(row.ItemId, row.Payload),
                state => new SavedMutatedItem { ItemId = state.ItemId, Payload = state.Payload },
                ProjectMutatedItem,
                "ItemId,Payload",
                reader => new(reader.GetInt64(1), reader.GetString(2)),
                state => new(state.ItemId),
                primaryColumn: "ItemId"
            )
        );

    private static void ProjectMutatedItem(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMutatedItem>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMutatedItem WHERE ItemId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMutatedItem(ItemId,Payload)
                SELECT json_extract(value,'$.ItemId'),json_extract(value,'$.Payload') FROM json_each({rows}) WHERE 1
                ON CONFLICT(ItemId) DO UPDATE SET Payload=excluded.Payload
                """
            );
        }
    }
}
