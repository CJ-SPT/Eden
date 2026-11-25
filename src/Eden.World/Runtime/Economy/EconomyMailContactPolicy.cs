using System.Text.Json;
using Eden.Data.World.Mail;
using Eden.World.Mail;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMailContactPolicy(long PilotId, long? CostIsk);

internal sealed partial class ResidentEconomy
{
    private void RegisterMailContactPolicy() =>
        Register(
            new EconomyTable<SavedMailContactPolicy, EconomyMailContactPolicy>(
                MailContactPolicySchema.Table,
                row => new(row.PilotId),
                row => new(row.PilotId, row.CostIsk),
                state => new SavedMailContactPolicy { PilotId = state.PilotId, CostIsk = state.CostIsk },
                ProjectMailContactPolicy,
                "PilotId,CostIsk",
                reader => new(reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)),
                state => new(state.PilotId),
                primaryColumn: "PilotId"
            )
        );

    private static void ProjectMailContactPolicy(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMailContactPolicy>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMailContactPolicy WHERE PilotId IN (SELECT value FROM json_each({removed}))");
            string saved = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<EconomyMailContactPolicy>());
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMailContactPolicy(PilotId,CostIsk)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.CostIsk') FROM json_each({saved}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET CostIsk=excluded.CostIsk
                """
            );
        }
    }
}
