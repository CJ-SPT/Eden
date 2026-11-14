using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyHackingContainer(
    long Id,
    long SiteId,
    int Index,
    int Failures,
    bool Hacked,
    bool Destroyed,
    bool Empty,
    long OwnerId,
    int LootSeed
);

internal sealed record EconomyHackingAttempt(long PilotId, long ContainerId, string ContactJson);

internal sealed partial class ResidentEconomy
{
    private void RegisterHacking()
    {
        Register(
            new EconomyTable<SavedHackingContainer, EconomyHackingContainer>(
                "WorldHackingContainer",
                row => new(row.Id),
                row => new(row.Id, row.SiteId, row.Index, row.Failures, row.Hacked, row.Destroyed, row.Empty, row.OwnerId, row.LootSeed),
                state => new SavedHackingContainer
                {
                    Id = state.Id,
                    SiteId = state.SiteId,
                    Index = state.Index,
                    Failures = state.Failures,
                    Hacked = state.Hacked,
                    Destroyed = state.Destroyed,
                    Empty = state.Empty,
                    OwnerId = state.OwnerId,
                    LootSeed = state.LootSeed,
                },
                ProjectHackingContainer,
                "Id,SiteId,\"Index\",Failures,Hacked,Destroyed,Empty,OwnerId,LootSeed",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetBoolean(5),
                        reader.GetBoolean(6),
                        reader.GetBoolean(7),
                        reader.GetInt64(8),
                        reader.GetInt32(9)
                    ),
                state => new(state.Id),
                indexes: [new("SiteId", row => row.SiteId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedHackingAttempt, EconomyHackingAttempt>(
                "WorldHackingAttempt",
                row => new(row.PilotId),
                row => new(row.PilotId, row.ContainerId, row.ContactJson),
                state => new SavedHackingAttempt
                {
                    PilotId = state.PilotId,
                    ContainerId = state.ContainerId,
                    ContactJson = state.ContactJson,
                },
                ProjectHackingAttempt,
                "PilotId,ContainerId,ContactJson",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)),
                state => new(state.PilotId),
                indexes: [new("ContainerId", row => row.ContainerId)],
                primaryColumn: "PilotId"
            )
        );
    }

    private static void ProjectHackingContainer(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyHackingContainer>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldHackingContainer WHERE Id IN (SELECT value FROM json_each({removed}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldHackingContainer(Id,SiteId,"Index",Failures,Hacked,Destroyed,Empty,OwnerId,LootSeed)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.SiteId'),json_extract(value,'$.Index'),
                    json_extract(value,'$.Failures'),json_extract(value,'$.Hacked'),json_extract(value,'$.Destroyed'),
                    json_extract(value,'$.Empty'),json_extract(value,'$.OwnerId'),json_extract(value,'$.LootSeed')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET Failures=excluded.Failures,
                    Hacked=excluded.Hacked,
                    Destroyed=excluded.Destroyed,
                    Empty=excluded.Empty,
                    OwnerId=excluded.OwnerId,
                    LootSeed=excluded.LootSeed
                """
            );
        }
    }

    private static void ProjectHackingAttempt(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyHackingAttempt>> changes)
    {
        // Two pilots can exchange container reservations before the next checkpoint.
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldHackingAttempt WHERE PilotId IN (SELECT value FROM json_each({removed}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldHackingAttempt(PilotId,ContainerId,ContactJson)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.ContainerId'),json_extract(value,'$.ContactJson')
                FROM json_each({values}) WHERE 1
                """
            );
        }
    }
}
