using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record OrganizationRow(long PilotId, string StateJson);

    private void RegisterCapsuleerOrganizations() =>
        Register(
            new EconomyTable<SavedCapsuleerOrganization, OrganizationRow>(
                "WorldCapsuleerOrganization",
                row => new(row.PilotId),
                row => new(row.PilotId, row.StateJson),
                row => new SavedCapsuleerOrganization { PilotId = row.PilotId, StateJson = row.StateJson },
                ProjectCapsuleerOrganizations,
                "PilotId,StateJson",
                reader => new(reader.GetInt64(1), reader.GetString(2)),
                row => new(row.PilotId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "PilotId"
            )
        );

    private static void ProjectCapsuleerOrganizations(WorldDbContext world, IReadOnlyList<EconomyChange<OrganizationRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCapsuleerOrganization WHERE PilotId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCapsuleerOrganization(PilotId,StateJson)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.StateJson')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET StateJson=excluded.StateJson
                """
            );
        }
    }
}
