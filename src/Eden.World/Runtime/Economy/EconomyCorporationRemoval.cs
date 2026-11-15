using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyCorporationRemoval(
    long PilotId,
    int CorporationId,
    long ActorId,
    long MembershipId,
    int DestinationCorporationId,
    long RequestedAt
);

internal sealed partial class ResidentEconomy
{
    private void RegisterCorporationRemovals() =>
        Register(
            new EconomyTable<SavedCorporationRemoval, EconomyCorporationRemoval>(
                "WorldCorporationRemoval",
                row => new(row.PilotId),
                row => new(row.PilotId, row.CorporationId, row.ActorId, row.MembershipId, row.DestinationCorporationId, row.RequestedAt),
                state => new SavedCorporationRemoval
                {
                    PilotId = state.PilotId,
                    CorporationId = state.CorporationId,
                    ActorId = state.ActorId,
                    MembershipId = state.MembershipId,
                    DestinationCorporationId = state.DestinationCorporationId,
                    RequestedAt = state.RequestedAt,
                },
                ProjectCorporationRemovals,
                "PilotId,CorporationId,ActorId,MembershipId,DestinationCorporationId,RequestedAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt32(5),
                        reader.GetInt64(6)
                    ),
                state => new(state.PilotId),
                indexes: [new("CorporationId", row => row.CorporationId)],
                primaryColumn: "PilotId"
            )
        );

    private static void ProjectCorporationRemovals(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyCorporationRemoval>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldCorporationRemoval WHERE PilotId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationRemoval(PilotId,CorporationId,ActorId,MembershipId,DestinationCorporationId,RequestedAt)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.CorporationId'),json_extract(value,'$.ActorId'),
                    json_extract(value,'$.MembershipId'),json_extract(value,'$.DestinationCorporationId'),
                    json_extract(value,'$.RequestedAt') FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET CorporationId=excluded.CorporationId,ActorId=excluded.ActorId,
                    MembershipId=excluded.MembershipId,DestinationCorporationId=excluded.DestinationCorporationId,
                    RequestedAt=excluded.RequestedAt
                """
            );
        }
    }
}
