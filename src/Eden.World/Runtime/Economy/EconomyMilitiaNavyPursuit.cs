using System.Text.Json;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMilitiaNavyPursuit(
    long PilotId,
    long ShipId,
    int SystemId,
    MilitiaFaction Faction,
    long RespondAt,
    long? FrigateId,
    long? CruiserId
);

internal sealed partial class ResidentEconomy
{
    private void RegisterMilitiaNavyPursuit() =>
        Register(
            new EconomyTable<SavedMilitiaNavyPursuit, EconomyMilitiaNavyPursuit>(
                "WorldMilitiaNavyPursuit",
                row => new(row.PilotId),
                row => new(row.PilotId, row.ShipId, row.SystemId, row.Faction, row.RespondAt, row.FrigateId, row.CruiserId),
                state =>
                    new()
                    {
                        PilotId = state.PilotId,
                        ShipId = state.ShipId,
                        SystemId = state.SystemId,
                        Faction = state.Faction,
                        RespondAt = state.RespondAt,
                        FrigateId = state.FrigateId,
                        CruiserId = state.CruiserId,
                    },
                ProjectMilitiaNavyPursuit,
                "PilotId,ShipId,SystemId,Faction,RespondAt,FrigateId,CruiserId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        (MilitiaFaction)reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7)
                    ),
                state => new(state.PilotId),
                primaryColumn: "PilotId"
            )
        );

    private static void ProjectMilitiaNavyPursuit(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaNavyPursuit>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMilitiaNavyPursuit WHERE PilotId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaNavyPursuit(PilotId,ShipId,SystemId,Faction,RespondAt,FrigateId,CruiserId)
                SELECT json_extract(value,'$.PilotId'), json_extract(value,'$.ShipId'), json_extract(value,'$.SystemId'),
                    json_extract(value,'$.Faction'), json_extract(value,'$.RespondAt'),
                    json_extract(value,'$.FrigateId'), json_extract(value,'$.CruiserId')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET ShipId=excluded.ShipId, SystemId=excluded.SystemId,
                    Faction=excluded.Faction, RespondAt=excluded.RespondAt, FrigateId=excluded.FrigateId, CruiserId=excluded.CruiserId
                """
            );
        }
    }
}
