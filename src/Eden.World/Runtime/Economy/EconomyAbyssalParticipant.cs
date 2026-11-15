using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyAbyssalParticipant(long PilotId, long RunId, long ShipId, bool Finished, long OutsideSince);

internal sealed partial class ResidentEconomy
{
    private void RegisterAbyssalParticipant() =>
        Register(
            new EconomyTable<SavedAbyssalParticipant, EconomyAbyssalParticipant>(
                "WorldAbyssalParticipant",
                row => new(row.PilotId),
                row => new(row.PilotId, row.RunId, row.ShipId, row.Finished, row.OutsideSince),
                state => new SavedAbyssalParticipant
                {
                    PilotId = state.PilotId,
                    RunId = state.RunId,
                    ShipId = state.ShipId,
                    Finished = state.Finished,
                    OutsideSince = state.OutsideSince,
                },
                ProjectAbyssalParticipant,
                "PilotId,RunId,ShipId,Finished,OutsideSince",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetBoolean(4), reader.GetInt64(5)),
                state => new(state.PilotId),
                indexes: [new("PilotId", row => row.PilotId), new("RunId", row => row.RunId), new("ShipId", row => row.ShipId)],
                primaryColumn: "PilotId"
            )
        );

    private static void ProjectAbyssalParticipant(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyAbyssalParticipant>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldAbyssalParticipant WHERE PilotId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAbyssalParticipant(PilotId,RunId,ShipId,Finished,OutsideSince)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.RunId'),
                    json_extract(value,'$.ShipId'),json_extract(value,'$.Finished'),json_extract(value,'$.OutsideSince')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET RunId=excluded.RunId,ShipId=excluded.ShipId,
                    Finished=excluded.Finished,OutsideSince=excluded.OutsideSince
                """
            );
        }
    }
}
