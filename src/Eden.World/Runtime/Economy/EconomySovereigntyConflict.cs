using System.Text.Json;
using Eden.Data.World.Sovereignty;
using Eden.Gameplay.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySovereigntyConflict(
    int SystemId,
    long HubId,
    int ConstellationId,
    int DefenderAllianceId,
    SovereigntyPhase Phase,
    long WindowStart,
    long WindowEnd,
    long? CampaignAt,
    int DefenderPoints,
    long SecureUntil,
    int Sequence,
    long NextSpawnAt,
    double Value,
    long UpdatedAt,
    int? ScoringTeam,
    double Rate,
    bool Blocked,
    long DevelopmentAt,
    double AppliedAdm,
    double WindowAdm,
    double FollowingAdm,
    long WindowCenter,
    long NextWindowLockAt,
    int ScheduledPrimeHour,
    double CampaignAdm
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSovereigntyConflict() =>
        Register(
            new EconomyTable<SavedSovereigntyConflict, EconomySovereigntyConflict>(
                "WorldSovereigntyConflict",
                row => new(row.SystemId),
                row =>
                    new(
                        row.SystemId,
                        row.HubId,
                        row.ConstellationId,
                        row.DefenderAllianceId,
                        row.Phase,
                        row.WindowStart,
                        row.WindowEnd,
                        row.CampaignAt,
                        row.DefenderPoints,
                        row.SecureUntil,
                        row.Sequence,
                        row.NextSpawnAt,
                        row.Value,
                        row.UpdatedAt,
                        row.ScoringTeam,
                        row.Rate,
                        row.Blocked,
                        row.DevelopmentAt,
                        row.AppliedAdm,
                        row.WindowAdm,
                        row.FollowingAdm,
                        row.WindowCenter,
                        row.NextWindowLockAt,
                        row.ScheduledPrimeHour,
                        row.CampaignAdm
                    ),
                state => new SavedSovereigntyConflict
                {
                    SystemId = state.SystemId,
                    HubId = state.HubId,
                    ConstellationId = state.ConstellationId,
                    DefenderAllianceId = state.DefenderAllianceId,
                    Phase = state.Phase,
                    WindowStart = state.WindowStart,
                    WindowEnd = state.WindowEnd,
                    CampaignAt = state.CampaignAt,
                    DefenderPoints = state.DefenderPoints,
                    SecureUntil = state.SecureUntil,
                    Sequence = state.Sequence,
                    NextSpawnAt = state.NextSpawnAt,
                    Value = state.Value,
                    UpdatedAt = state.UpdatedAt,
                    ScoringTeam = state.ScoringTeam,
                    Rate = state.Rate,
                    Blocked = state.Blocked,
                    DevelopmentAt = state.DevelopmentAt,
                    AppliedAdm = state.AppliedAdm,
                    WindowAdm = state.WindowAdm,
                    FollowingAdm = state.FollowingAdm,
                    WindowCenter = state.WindowCenter,
                    NextWindowLockAt = state.NextWindowLockAt,
                    ScheduledPrimeHour = state.ScheduledPrimeHour,
                    CampaignAdm = state.CampaignAdm,
                },
                ProjectSovereigntyConflict,
                "SystemId,HubId,ConstellationId,DefenderAllianceId,Phase,WindowStart,WindowEnd,CampaignAt,DefenderPoints,Secure"
                    + "Until,Sequence,NextSpawnAt,Value,UpdatedAt,ScoringTeam,Rate,Blocked,DevelopmentAt,AppliedAdm,WindowAdm,Followi"
                    + "ngAdm,WindowCenter,NextWindowLockAt,ScheduledPrimeHour,CampaignAdm",
                reader =>
                    new(
                        reader.GetInt32(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        (SovereigntyPhase)reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        reader.GetInt32(9),
                        reader.GetInt64(10),
                        reader.GetInt32(11),
                        reader.GetInt64(12),
                        reader.GetDouble(13),
                        reader.GetInt64(14),
                        reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        reader.GetDouble(16),
                        reader.GetBoolean(17),
                        reader.GetInt64(18),
                        reader.GetDouble(19),
                        reader.GetDouble(20),
                        reader.GetDouble(21),
                        reader.GetInt64(22),
                        reader.GetInt64(23),
                        reader.GetInt32(24),
                        reader.GetDouble(25)
                    ),
                state => new(state.SystemId),
                indexes:
                [
                    new("HubId", row => row.HubId),
                    new("ConstellationId", row => row.ConstellationId),
                    new("DefenderAllianceId", row => row.DefenderAllianceId),
                ],
                primaryColumn: "SystemId"
            )
        );

    private static void ProjectSovereigntyConflict(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySovereigntyConflict>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSovereigntyConflict WHERE SystemId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSovereigntyConflict(
                    SystemId,
                    HubId,
                    ConstellationId,
                    DefenderAllianceId,
                    Phase,
                    WindowStart,
                    WindowEnd,
                    CampaignAt,
                    DefenderPoints,
                    SecureUntil,
                    Sequence,
                    NextSpawnAt,
                    Value,
                    UpdatedAt,
                    ScoringTeam,
                    Rate,
                    Blocked,
                    DevelopmentAt,
                    AppliedAdm,
                    WindowAdm,
                    FollowingAdm,
                    WindowCenter,
                    NextWindowLockAt,
                    ScheduledPrimeHour,
                    CampaignAdm)
                SELECT
                    json_extract(value,'$.SystemId'),
                    json_extract(value,'$.HubId'),
                    json_extract(value,'$.ConstellationId'),
                    json_extract(value,'$.DefenderAllianceId'),
                    json_extract(value,'$.Phase'),
                    json_extract(value,'$.WindowStart'),
                    json_extract(value,'$.WindowEnd'),
                    json_extract(value,'$.CampaignAt'),
                    json_extract(value,'$.DefenderPoints'),
                    json_extract(value,'$.SecureUntil'),
                    json_extract(value,'$.Sequence'),
                    json_extract(value,'$.NextSpawnAt'),
                    json_extract(value,'$.Value'),
                    json_extract(value,'$.UpdatedAt'),
                    json_extract(value,'$.ScoringTeam'),
                    json_extract(value,'$.Rate'),
                    json_extract(value,'$.Blocked'),
                    json_extract(value,'$.DevelopmentAt'),
                    json_extract(value,'$.AppliedAdm'),
                    json_extract(value,'$.WindowAdm'),
                    json_extract(value,'$.FollowingAdm'),
                    json_extract(value,'$.WindowCenter'),
                    json_extract(value,'$.NextWindowLockAt'),
                    json_extract(value,'$.ScheduledPrimeHour'),
                    json_extract(value,'$.CampaignAdm')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SystemId) DO UPDATE SET
                    HubId=excluded.HubId,
                    ConstellationId=excluded.ConstellationId,
                    DefenderAllianceId=excluded.DefenderAllianceId,
                    Phase=excluded.Phase,
                    WindowStart=excluded.WindowStart,
                    WindowEnd=excluded.WindowEnd,
                    CampaignAt=excluded.CampaignAt,
                    DefenderPoints=excluded.DefenderPoints,
                    SecureUntil=excluded.SecureUntil,
                    Sequence=excluded.Sequence,
                    NextSpawnAt=excluded.NextSpawnAt,
                    Value=excluded.Value,
                    UpdatedAt=excluded.UpdatedAt,
                    ScoringTeam=excluded.ScoringTeam,
                    Rate=excluded.Rate,
                    Blocked=excluded.Blocked,
                    DevelopmentAt=excluded.DevelopmentAt,
                    AppliedAdm=excluded.AppliedAdm,
                    WindowAdm=excluded.WindowAdm,
                    FollowingAdm=excluded.FollowingAdm,
                    WindowCenter=excluded.WindowCenter,
                    NextWindowLockAt=excluded.NextWindowLockAt,
                    ScheduledPrimeHour=excluded.ScheduledPrimeHour,
                    CampaignAdm=excluded.CampaignAdm
                """
            );
        }
    }
}
