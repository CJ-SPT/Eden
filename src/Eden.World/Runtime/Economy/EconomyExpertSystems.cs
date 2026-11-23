using System.Text.Json;
using Eden.Data.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record EconomySavedExpertSystem(long PilotId, int TypeId, long InstalledAt, long ExpiresAt);

    private void RegisterExpertSystems() =>
        Register(
            new EconomyTable<SavedExpertSystem, EconomySavedExpertSystem>(
                "WorldExpertSystem",
                row => new(row.PilotId, row.TypeId),
                row => new(row.PilotId, row.TypeId, row.InstalledAt, row.ExpiresAt),
                state => new SavedExpertSystem
                {
                    PilotId = state.PilotId,
                    TypeId = state.TypeId,
                    InstalledAt = state.InstalledAt,
                    ExpiresAt = state.ExpiresAt,
                },
                ProjectExpertSystems,
                "PilotId,TypeId,InstalledAt,ExpiresAt",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)),
                state => new(state.PilotId, state.TypeId),
                indexes: [new(nameof(SavedExpertSystem.PilotId), state => state.PilotId)],
                primaryColumn: null
            )
        );

    private static void ProjectExpertSystems(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedExpertSystem>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldExpertSystem WHERE EXISTS (
                    SELECT 1 FROM json_each({removed})
                    WHERE PilotId=json_extract(value,'$.Id') AND TypeId=json_extract(value,'$.Part'))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldExpertSystem(PilotId,TypeId,InstalledAt,ExpiresAt)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.TypeId'),
                    json_extract(value,'$.InstalledAt'),json_extract(value,'$.ExpiresAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId,TypeId) DO UPDATE SET InstalledAt=excluded.InstalledAt,ExpiresAt=excluded.ExpiresAt
                """
            );
        }
    }
}
