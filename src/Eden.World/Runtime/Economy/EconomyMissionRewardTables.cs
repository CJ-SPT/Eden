using System.Text.Json;
using Eden.Data.World.Agents;
using Eden.Data.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedLoyaltyBalance(long PilotId, int IssuerCorporationId, long Points);

internal sealed record EconomySavedAgentDecline(long PilotId, long AgentId, long LastFreeDeclineAt);

internal sealed record EconomySavedMissionProgress(long PilotId, int FactionId, int Level, int Completed);

internal sealed partial class ResidentEconomy
{
    // The third key field is the mission level (a catalog level 1 through 5).
    private const int MissionLevelKeyStride = Eden.Gameplay.Agents.MissionRules.MaximumAgentLevel + 1;

    private static EconomyKey MissionProgressKey(long pilot, int faction, int level) =>
        new(pilot, checked((long)faction * MissionLevelKeyStride + level));

    private void RegisterMissionRewardTables()
    {
        Register(
            new EconomyTable<SavedLoyaltyBalance, EconomySavedLoyaltyBalance>(
                "WorldLoyaltyWallet",
                row => new(row.PilotId, row.IssuerCorporationId),
                row => new(row.PilotId, row.IssuerCorporationId, row.Points),
                state => new SavedLoyaltyBalance
                {
                    PilotId = state.PilotId,
                    IssuerCorporationId = state.IssuerCorporationId,
                    Points = state.Points,
                },
                ProjectSavedLoyaltyBalance,
                "PilotId,IssuerCorporationId,Points",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3)),
                state => new(state.PilotId, state.IssuerCorporationId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedAgentDecline, EconomySavedAgentDecline>(
                "WorldAgentDecline",
                row => new(row.PilotId, row.AgentId),
                row => new(row.PilotId, row.AgentId, row.LastFreeDeclineAt),
                state => new SavedAgentDecline
                {
                    PilotId = state.PilotId,
                    AgentId = state.AgentId,
                    LastFreeDeclineAt = state.LastFreeDeclineAt,
                },
                ProjectSavedAgentDecline,
                "PilotId,AgentId,LastFreeDeclineAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
                state => new(state.PilotId, state.AgentId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedMissionProgress, EconomySavedMissionProgress>(
                "WorldMissionProgress",
                row => MissionProgressKey(row.PilotId, row.FactionId, row.Level),
                row => new(row.PilotId, row.FactionId, row.Level, row.Completed),
                state => new SavedMissionProgress
                {
                    PilotId = state.PilotId,
                    FactionId = state.FactionId,
                    Level = state.Level,
                    Completed = state.Completed,
                },
                ProjectSavedMissionProgress,
                "PilotId,FactionId,Level,Completed",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)),
                state => MissionProgressKey(state.PilotId, state.FactionId, state.Level),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectSavedLoyaltyBalance(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedLoyaltyBalance>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldLoyaltyWallet
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                    WorldLoyaltyWallet.PilotId=json_extract(value,'$.Id') AND
                    WorldLoyaltyWallet.IssuerCorporationId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldLoyaltyWallet(PilotId,IssuerCorporationId,Points)
                SELECT json_extract(value,'$.PilotId'),
                    json_extract(value,'$.IssuerCorporationId'),
                    json_extract(value,'$.Points')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,IssuerCorporationId) DO UPDATE SET Points=excluded.Points
                """
            );
        }
    }

    private static void ProjectSavedAgentDecline(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedAgentDecline>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldAgentDecline
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                    WorldAgentDecline.PilotId=json_extract(value,'$.Id') AND
                    WorldAgentDecline.AgentId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAgentDecline(PilotId,AgentId,LastFreeDeclineAt)
                SELECT json_extract(value,'$.PilotId'),
                    json_extract(value,'$.AgentId'),
                    json_extract(value,'$.LastFreeDeclineAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,AgentId) DO UPDATE SET LastFreeDeclineAt=excluded.LastFreeDeclineAt
                """
            );
        }
    }

    private static void ProjectSavedMissionProgress(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMissionProgress>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMissionProgress
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                    WorldMissionProgress.PilotId=json_extract(value,'$.Id') AND
                    WorldMissionProgress.FactionId*{MissionLevelKeyStride}+WorldMissionProgress.Level=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMissionProgress(PilotId,FactionId,Level,Completed)
                SELECT json_extract(value,'$.PilotId'),
                    json_extract(value,'$.FactionId'),
                    json_extract(value,'$.Level'),
                    json_extract(value,'$.Completed')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,FactionId,Level) DO UPDATE SET Completed=excluded.Completed
                """
            );
        }
    }
}
