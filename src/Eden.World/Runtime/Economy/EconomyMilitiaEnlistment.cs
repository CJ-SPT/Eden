using System.Text.Json;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMilitiaEnlistment(long PilotId, MilitiaFaction? DirectFaction, long DirectCooldownUntil);

internal sealed partial class ResidentEconomy
{
    private void RegisterMilitiaEnlistment() =>
        Register(
            new EconomyTable<SavedMilitiaEnlistment, EconomyMilitiaEnlistment>(
                "WorldMilitiaEnlistment",
                row => new(row.PilotId),
                row => new(row.PilotId, row.DirectFaction, row.DirectCooldownUntil),
                state =>
                    new()
                    {
                        PilotId = state.PilotId,
                        DirectFaction = state.DirectFaction,
                        DirectCooldownUntil = state.DirectCooldownUntil,
                    },
                ProjectMilitiaEnlistment,
                "PilotId,DirectFaction,DirectCooldownUntil",
                reader => new(reader.GetInt64(1), reader.IsDBNull(2) ? null : (MilitiaFaction)reader.GetInt32(2), reader.GetInt64(3)),
                state => new(state.PilotId),
                primaryColumn: "PilotId"
            )
        );

    private static void ProjectMilitiaEnlistment(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaEnlistment>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMilitiaEnlistment WHERE PilotId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaEnlistment(PilotId,DirectFaction,DirectCooldownUntil)
                SELECT json_extract(value,'$.PilotId'), json_extract(value,'$.DirectFaction'), json_extract(value,'$.DirectCooldownUntil')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET
                    DirectFaction=excluded.DirectFaction, DirectCooldownUntil=excluded.DirectCooldownUntil
                """
            );
        }
    }
}
