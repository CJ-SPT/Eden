using System.Text.Json;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMilitiaObjective(
    long Id,
    int SystemId,
    MilitiaObjectiveKind Kind,
    MilitiaFaction Faction,
    long? PilotId,
    long? TargetId,
    int Generation,
    long ReadyAt,
    long ExpiresAt,
    long? ReplaceAt,
    long Elapsed,
    int Waves
);

internal sealed partial class ResidentEconomy
{
    private void RegisterMilitiaObjectives() =>
        Register(
            new EconomyTable<SavedMilitiaObjective, EconomyMilitiaObjective>(
                "WorldMilitiaObjective",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.SystemId,
                        row.Kind,
                        row.Faction,
                        row.PilotId,
                        row.TargetId,
                        row.Generation,
                        row.ReadyAt,
                        row.ExpiresAt,
                        row.ReplaceAt,
                        row.Elapsed,
                        row.Waves
                    ),
                state =>
                    new()
                    {
                        Id = state.Id,
                        SystemId = state.SystemId,
                        Kind = state.Kind,
                        Faction = state.Faction,
                        PilotId = state.PilotId,
                        TargetId = state.TargetId,
                        Generation = state.Generation,
                        ReadyAt = state.ReadyAt,
                        ExpiresAt = state.ExpiresAt,
                        ReplaceAt = state.ReplaceAt,
                        Elapsed = state.Elapsed,
                        Waves = state.Waves,
                    },
                ProjectMilitiaObjectives,
                "Id,SystemId,Kind,Faction,PilotId,TargetId,Generation,ReadyAt,ExpiresAt,ReplaceAt,Elapsed,Waves",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        (MilitiaObjectiveKind)reader.GetInt32(3),
                        (MilitiaFaction)reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.GetInt32(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt64(10),
                        reader.GetInt64(11),
                        reader.GetInt32(12)
                    ),
                state => new(state.Id),
                indexes: [new(nameof(SavedMilitiaObjective.SystemId), state => state.SystemId)],
                primaryColumn: "Id"
            )
        );

    private static void ProjectMilitiaObjectives(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaObjective>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMilitiaObjective WHERE Id IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaObjective(Id,SystemId,Kind,Faction,PilotId,TargetId,Generation,ReadyAt,ExpiresAt,
                    ReplaceAt,Elapsed,Waves)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.SystemId'), json_extract(value,'$.Kind'),
                    json_extract(value,'$.Faction'), json_extract(value,'$.PilotId'), json_extract(value,'$.TargetId'),
                    json_extract(value,'$.Generation'), json_extract(value,'$.ReadyAt'), json_extract(value,'$.ExpiresAt'),
                    json_extract(value,'$.ReplaceAt'), json_extract(value,'$.Elapsed'),
                    json_extract(value,'$.Waves')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET TargetId=excluded.TargetId,Generation=excluded.Generation,
                    ReadyAt=excluded.ReadyAt,ExpiresAt=excluded.ExpiresAt,ReplaceAt=excluded.ReplaceAt,
                    Elapsed=excluded.Elapsed,Waves=excluded.Waves
                """
            );
        }
    }
}
