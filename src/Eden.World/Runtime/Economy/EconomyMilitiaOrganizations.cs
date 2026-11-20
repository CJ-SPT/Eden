using System.Text.Json;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMilitiaOrganization(
    int OwnerId,
    MilitiaOrganizationKind Kind,
    MilitiaFaction Faction,
    MilitiaOrganizationPhase Phase,
    long EffectiveAt,
    long? StartedAt,
    long ReviewedAt,
    long? StandingExpiry
);

internal sealed record EconomyMilitiaStandingWarning(long PilotId, int OwnerId, int CorporationId, MilitiaFaction Faction, long ExpiresAt);

internal sealed partial class ResidentEconomy
{
    private void RegisterMilitiaOrganizations()
    {
        Register(
            new EconomyTable<SavedMilitiaOrganization, EconomyMilitiaOrganization>(
                "WorldMilitiaOrganization",
                row => new(row.OwnerId),
                row =>
                    new(row.OwnerId, row.Kind, row.Faction, row.Phase, row.EffectiveAt, row.StartedAt, row.ReviewedAt, row.StandingExpiry),
                state =>
                    new()
                    {
                        OwnerId = state.OwnerId,
                        Kind = state.Kind,
                        Faction = state.Faction,
                        Phase = state.Phase,
                        EffectiveAt = state.EffectiveAt,
                        StartedAt = state.StartedAt,
                        ReviewedAt = state.ReviewedAt,
                        StandingExpiry = state.StandingExpiry,
                    },
                ProjectMilitiaOrganizations,
                "OwnerId,Kind,Faction,Phase,EffectiveAt,StartedAt,ReviewedAt,StandingExpiry",
                reader =>
                    new(
                        reader.GetInt32(1),
                        (MilitiaOrganizationKind)reader.GetInt32(2),
                        (MilitiaFaction)reader.GetInt32(3),
                        (MilitiaOrganizationPhase)reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8)
                    ),
                state => new(state.OwnerId),
                primaryColumn: "OwnerId"
            )
        );
        Register(
            new EconomyTable<SavedMilitiaStandingWarning, EconomyMilitiaStandingWarning>(
                "WorldMilitiaStandingWarning",
                row => new(row.PilotId),
                row => new(row.PilotId, row.OwnerId, row.CorporationId, row.Faction, row.ExpiresAt),
                state =>
                    new()
                    {
                        PilotId = state.PilotId,
                        OwnerId = state.OwnerId,
                        CorporationId = state.CorporationId,
                        Faction = state.Faction,
                        ExpiresAt = state.ExpiresAt,
                    },
                ProjectMilitiaStandingWarnings,
                "PilotId,OwnerId,CorporationId,Faction,ExpiresAt",
                reader =>
                    new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3), (MilitiaFaction)reader.GetInt32(4), reader.GetInt64(5)),
                state => new(state.PilotId),
                indexes: [new("OwnerId", state => state.OwnerId)],
                primaryColumn: "PilotId"
            )
        );
    }

    private static void ProjectMilitiaOrganizations(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaOrganization>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMilitiaOrganization WHERE OwnerId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaOrganization(OwnerId,Kind,Faction,Phase,EffectiveAt,StartedAt,ReviewedAt,StandingExpiry)
                SELECT json_extract(value,'$.OwnerId'),json_extract(value,'$.Kind'),json_extract(value,'$.Faction'),
                    json_extract(value,'$.Phase'),json_extract(value,'$.EffectiveAt'),json_extract(value,'$.StartedAt'),
                    json_extract(value,'$.ReviewedAt'),json_extract(value,'$.StandingExpiry')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(OwnerId) DO UPDATE SET Kind=excluded.Kind,Faction=excluded.Faction,Phase=excluded.Phase,
                    EffectiveAt=excluded.EffectiveAt,StartedAt=excluded.StartedAt,ReviewedAt=excluded.ReviewedAt,
                    StandingExpiry=excluded.StandingExpiry
                """
            );
        }
    }

    private static void ProjectMilitiaStandingWarnings(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomyMilitiaStandingWarning>> changes
    )
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string removed = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMilitiaStandingWarning WHERE PilotId IN (SELECT value FROM json_each({removed}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaStandingWarning(PilotId,OwnerId,CorporationId,Faction,ExpiresAt)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.OwnerId'),json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.Faction'),json_extract(value,'$.ExpiresAt')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET OwnerId=excluded.OwnerId,CorporationId=excluded.CorporationId,
                    Faction=excluded.Faction,ExpiresAt=excluded.ExpiresAt
                """
            );
        }
    }
}
