using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record ActivityRewardTickRow(
        long PilotId,
        long DueAt,
        long SkillPoints,
        ImmutableSortedDictionary<int, SavedActivityBounty> Bounties
    );

    private readonly SortedSet<(long DueAt, long PilotId)> _activityRewardDeadlines = [];

    internal (long DueAt, long PilotId)[] DueActivityRewards(long now)
    {
        using var owner = _owner.EnterShared("activity reward tick deadlines");
        EnsureLoaded();
        return _activityRewardDeadlines.Count == 0 || _activityRewardDeadlines.Min.DueAt > now
            ? []
            : _activityRewardDeadlines.TakeWhile(tick => tick.DueAt <= now).ToArray();
    }

    private void ActivityRewardTickChanged(ActivityRewardTickRow? before, ActivityRewardTickRow? after)
    {
        if (before is null && after is not null)
        {
            _activityRewardDeadlines.Add((after.DueAt, after.PilotId));
        }
        else if (before is not null && after is null)
        {
            _activityRewardDeadlines.Remove((before.DueAt, before.PilotId));
        }
    }

    private void RegisterActivityRewardTicks()
    {
        Register(
            new EconomyTable<SavedActivityRewardTick, ActivityRewardTickRow>(
                "WorldActivityRewardTick",
                row => new(row.PilotId, row.DueAt),
                row => new(row.PilotId, row.DueAt, row.SkillPoints, row.Bounties),
                state => new SavedActivityRewardTick
                {
                    PilotId = state.PilotId,
                    DueAt = state.DueAt,
                    SkillPoints = state.SkillPoints,
                    Bounties = state.Bounties,
                },
                ProjectActivityRewardTicks,
                "PilotId,DueAt,SkillPoints,Bounties",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        JsonSerializer.Deserialize<ImmutableSortedDictionary<int, SavedActivityBounty>>(reader.GetString(4))
                            ?? throw new InvalidDataException("Missing saved bounty totals.")
                    ),
                state => new(state.PilotId, state.DueAt),
                indexes: [new("PilotId", row => row.PilotId)],
                changed: ActivityRewardTickChanged,
                primaryColumn: null
            )
        );
    }

    private static void ProjectActivityRewardTicks(WorldDbContext world, IReadOnlyList<EconomyChange<ActivityRewardTickRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key).ToArray();
            if (removed.Length != 0)
            {
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldActivityRewardTick
                    WHERE (PilotId,DueAt) IN (SELECT value->>'$.Id',value->>'$.Part' FROM json_each({JsonSerializer.Serialize(removed)}))
                    """
                );
            }
            var rows = batch.Where(change => change.Value is not null).Select(change => change.Value).ToArray();
            if (rows.Length != 0)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldActivityRewardTick(PilotId,DueAt,SkillPoints,Bounties)
                    SELECT value->>'$.PilotId',value->>'$.DueAt',value->>'$.SkillPoints',value->'$.Bounties'
                    FROM json_each({JsonSerializer.Serialize(rows)}) WHERE 1
                    ON CONFLICT(PilotId,DueAt) DO UPDATE SET SkillPoints=excluded.SkillPoints,Bounties=excluded.Bounties
                    """
                );
            }
        }
    }
}
