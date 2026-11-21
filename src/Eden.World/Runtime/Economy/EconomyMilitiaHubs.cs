using System.Text.Json;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMilitiaHub(int SystemId, long NpcId, MilitiaFaction? Attacker, long? TransferAt);

internal sealed record EconomyMilitiaHubContribution(long NpcId, long PilotId, double Damage);

internal sealed partial class ResidentEconomy
{
    private void RegisterMilitiaHubs()
    {
        Register(
            new EconomyTable<SavedMilitiaHub, EconomyMilitiaHub>(
                "WorldMilitiaHub",
                row => new(row.SystemId),
                row => new(row.SystemId, row.NpcId, row.Attacker, row.TransferAt),
                state =>
                    new()
                    {
                        SystemId = state.SystemId,
                        NpcId = state.NpcId,
                        Attacker = state.Attacker,
                        TransferAt = state.TransferAt,
                    },
                ProjectMilitiaHubs,
                "SystemId,NpcId,Attacker,TransferAt",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : (MilitiaFaction)reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4)
                    ),
                state => new(state.SystemId),
                changed: (before, after) =>
                {
                    if (before?.TransferAt != after?.TransferAt)
                    {
                        Interlocked.Increment(ref _warzoneRevision);
                    }
                },
                primaryColumn: "SystemId"
            )
        );
        Register(
            new EconomyTable<SavedMilitiaHubContribution, EconomyMilitiaHubContribution>(
                "WorldMilitiaHubContribution",
                row => new(row.NpcId, row.PilotId),
                row => new(row.NpcId, row.PilotId, row.Damage),
                state =>
                    new()
                    {
                        NpcId = state.NpcId,
                        PilotId = state.PilotId,
                        Damage = state.Damage,
                    },
                ProjectMilitiaHubContributions,
                "NpcId,PilotId,Damage",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3)),
                state => new(state.NpcId, state.PilotId),
                indexes: [new("NpcId", state => state.NpcId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectMilitiaHubs(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaHub>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMilitiaHub WHERE SystemId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaHub(SystemId,NpcId,Attacker,TransferAt)
                SELECT json_extract(value,'$.SystemId'), json_extract(value,'$.NpcId'),
                    json_extract(value,'$.Attacker'), json_extract(value,'$.TransferAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SystemId) DO UPDATE SET NpcId=excluded.NpcId, Attacker=excluded.Attacker, TransferAt=excluded.TransferAt
                """
            );
        }
    }

    private static void ProjectMilitiaHubContributions(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyMilitiaHubContribution>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMilitiaHubContribution WHERE (NpcId,PilotId) IN
                    (SELECT json_extract(value,'$.Id'), json_extract(value,'$.Part') FROM json_each({removed}))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaHubContribution(NpcId,PilotId,Damage)
                SELECT json_extract(value,'$.NpcId'), json_extract(value,'$.PilotId'), json_extract(value,'$.Damage')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(NpcId,PilotId) DO UPDATE SET Damage=excluded.Damage
                """
            );
        }
    }
}
