using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Agents;
using Eden.Data.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedLoyaltyBalance(long PilotId, int IssuerCorporationId, long Points);

internal sealed record EconomySavedAgentDecline(long PilotId, long AgentId, long LastFreeDeclineAt);

internal sealed record EconomySavedMissionGateUnlock(long MissionId, long GateObjectId, long PilotId, long ResetAt);

internal sealed record EconomySavedResearchProject(
    long Id,
    long PilotId,
    long AgentId,
    int SkillId,
    double Points,
    double PointsPerDay,
    long SettledAt
);

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
            new EconomyTable<SavedResearchProject, EconomySavedResearchProject>(
                "WorldResearchProject",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.AgentId, row.SkillId, row.Points, row.PointsPerDay, row.SettledAt),
                state => new SavedResearchProject
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    AgentId = state.AgentId,
                    SkillId = state.SkillId,
                    Points = state.Points,
                    PointsPerDay = state.PointsPerDay,
                    SettledAt = state.SettledAt,
                },
                ProjectSavedResearchProject,
                "Id,PilotId,AgentId,SkillId,Points,PointsPerDay,SettledAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetDouble(5),
                        reader.GetDouble(6),
                        reader.GetInt64(7)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedMissionGateUnlock, EconomySavedMissionGateUnlock>(
                "WorldMissionGateUnlock",
                row => new(row.MissionId, row.GateObjectId, row.PilotId.ToString(CultureInfo.InvariantCulture)),
                row => new(row.MissionId, row.GateObjectId, row.PilotId, row.ResetAt),
                state => new SavedMissionGateUnlock
                {
                    MissionId = state.MissionId,
                    GateObjectId = state.GateObjectId,
                    PilotId = state.PilotId,
                    ResetAt = state.ResetAt,
                },
                ProjectSavedMissionGateUnlock,
                "MissionId,GateObjectId,PilotId,ResetAt",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
                state => new(state.MissionId, state.GateObjectId, state.PilotId.ToString(CultureInfo.InvariantCulture)),
                indexes: [new("MissionId", row => row.MissionId), new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
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

    private static void ProjectSavedMissionGateUnlock(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedMissionGateUnlock>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMissionGateUnlock WHERE EXISTS (
                        SELECT 1 FROM json_each({deleted})
                        WHERE MissionId=json_extract(value,'$.Id') AND GateObjectId=json_extract(value,'$.Part')
                            AND PilotId=CAST(json_extract(value,'$.Name') AS INTEGER))
                    """
                );
            }
            var rows = batch.Where(change => change.Value is not null).Select(change => change.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldMissionGateUnlock(MissionId,GateObjectId,PilotId,ResetAt)
                    SELECT json_extract(value,'$.MissionId'),json_extract(value,'$.GateObjectId'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.ResetAt')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(MissionId,GateObjectId,PilotId) DO UPDATE SET ResetAt=excluded.ResetAt
                    """
                );
            }
        }
    }

    private static void ProjectSavedResearchProject(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedResearchProject>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldResearchProject WHERE Id IN (SELECT value FROM json_each({deleted}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldResearchProject(Id,PilotId,AgentId,SkillId,Points,PointsPerDay,SettledAt)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),
                    json_extract(value,'$.AgentId'),json_extract(value,'$.SkillId'),
                    json_extract(value,'$.Points'),json_extract(value,'$.PointsPerDay'),json_extract(value,'$.SettledAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET Points=excluded.Points,PointsPerDay=excluded.PointsPerDay,SettledAt=excluded.SettledAt
                """
            );
        }
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
