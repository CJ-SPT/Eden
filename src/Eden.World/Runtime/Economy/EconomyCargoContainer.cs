using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyCargoContainer(
    long ItemId,
    string? Name,
    string? GeneralPassword,
    string? ConfigurationPassword,
    CargoContainerConfiguration Configuration
);

internal sealed partial class ResidentEconomy
{
    private void RegisterCargoContainer() =>
        Register(
            new EconomyTable<SavedCargoContainer, EconomyCargoContainer>(
                "WorldCargoContainer",
                row => new(row.ItemId),
                row => new(row.ItemId, row.Name, row.GeneralPassword, row.ConfigurationPassword, row.Configuration),
                state => new SavedCargoContainer
                {
                    ItemId = state.ItemId,
                    Name = state.Name,
                    GeneralPassword = state.GeneralPassword,
                    ConfigurationPassword = state.ConfigurationPassword,
                    Configuration = state.Configuration,
                },
                ProjectCargoContainer,
                "ItemId,Name,GeneralPassword,ConfigurationPassword,Configuration",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        (CargoContainerConfiguration)reader.GetInt32(5)
                    ),
                state => new(state.ItemId),
                primaryColumn: "ItemId"
            )
        );

    private static void ProjectCargoContainer(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyCargoContainer>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCargoContainer WHERE ItemId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCargoContainer(ItemId,Name,GeneralPassword,ConfigurationPassword,Configuration)
                SELECT json_extract(value,'$.ItemId'),json_extract(value,'$.Name'),json_extract(value,'$.GeneralPassword'),
                    json_extract(value,'$.ConfigurationPassword'),json_extract(value,'$.Configuration') FROM json_each({rows}) WHERE 1
                ON CONFLICT(ItemId) DO UPDATE SET Name=excluded.Name,GeneralPassword=excluded.GeneralPassword,
                    ConfigurationPassword=excluded.ConfigurationPassword,Configuration=excluded.Configuration
                """
            );
        }
    }
}
