using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.Data.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySleeperSite(
    long Id,
    long? ParentSiteId,
    int SystemId,
    long? ActivatedAt,
    long? ExpiresAt,
    int HackStarts,
    int HackFailures,
    int HackResolutions,
    int AggressedWave
);

internal sealed partial class ResidentEconomy
{
    private void RegisterSleeperSites() =>
        Register(
            new EconomyTable<SavedSleeperSite, EconomySleeperSite>(
                "WorldSleeperSite",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.ParentSiteId,
                        row.SystemId,
                        row.ActivatedAt,
                        row.ExpiresAt,
                        row.HackStarts,
                        row.HackFailures,
                        row.HackResolutions,
                        row.AggressedWave
                    ),
                state => new SavedSleeperSite
                {
                    Id = state.Id,
                    ParentSiteId = state.ParentSiteId,
                    SystemId = state.SystemId,
                    ActivatedAt = state.ActivatedAt,
                    ExpiresAt = state.ExpiresAt,
                    HackStarts = state.HackStarts,
                    HackFailures = state.HackFailures,
                    HackResolutions = state.HackResolutions,
                    AggressedWave = state.AggressedWave,
                },
                ProjectSleeperSites,
                "Id,ParentSiteId,SystemId,ActivatedAt,ExpiresAt,HackStarts,HackFailures,HackResolutions,AggressedWave",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.GetInt32(9)
                    ),
                state => new(state.Id),
                indexes: [new("ParentSiteId", row => row.ParentSiteId), new("SystemId", row => row.SystemId)]
            )
        );

    private static void ProjectSleeperSites(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySleeperSite>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(c => c.Value is null).Select(c => c.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSleeperSite WHERE Id IN (SELECT value FROM json_each({deleted}))");
            string values = JsonSerializer.Serialize(batch.Where(c => c.Value is not null).Select(c => c.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSleeperSite(
                    Id,ParentSiteId,SystemId,ActivatedAt,ExpiresAt,HackStarts,HackFailures,HackResolutions,AggressedWave)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.ParentSiteId'),json_extract(value,'$.SystemId'),
                    json_extract(value,'$.ActivatedAt'),json_extract(value,'$.ExpiresAt'),json_extract(value,'$.HackStarts'),
                    json_extract(value,'$.HackFailures'),json_extract(value,'$.HackResolutions'),json_extract(value,'$.AggressedWave')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET ParentSiteId=excluded.ParentSiteId,SystemId=excluded.SystemId,
                    ActivatedAt=excluded.ActivatedAt,ExpiresAt=excluded.ExpiresAt,HackStarts=excluded.HackStarts,
                    HackFailures=excluded.HackFailures,HackResolutions=excluded.HackResolutions,AggressedWave=excluded.AggressedWave
                """
            );
        }
    }
}

internal sealed partial class EconomyTransaction
{
    private void ValidateSleeperSites()
    {
        foreach (var row in Retained<SavedSleeperSite>())
        {
            if (
                row.HackStarts < 0
                || row.HackFailures < 0
                || row.HackResolutions < row.HackFailures
                || row.HackResolutions > row.HackStarts
                || row.AggressedWave < -1
                || Set<WorldCombatAnomaly>().Find(row.Id) is not { } site
                || site.SystemId != row.SystemId
                || row.AggressedWave > site.Wave
                || row.ExpiresAt.HasValue != row.ActivatedAt.HasValue
                || row.ExpiresAt <= row.ActivatedAt
            )
            {
                throw new InvalidDataException("Invalid resident Sleeper progress or encounter reference.");
            }
            if (
                row.ParentSiteId is long parent
                && Set<SavedExplorationSite>().Find(parent)?.SystemId != row.SystemId
                && Set<SavedMiningSite>().Find(parent)?.SystemId != row.SystemId
            )
            {
                throw new InvalidDataException("Missing resident Sleeper resource or exploration parent.");
            }
        }
    }
}
