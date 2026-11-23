using System.Text.Json;
using Eden.Data.World.Agents;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record MissionReinforcementRow(long MissionId, int Wave, int SystemId, long DueAt);

    private readonly SortedSet<(long DueAt, int SystemId, long MissionId, int Wave)> _missionReinforcements = [];
    private readonly Dictionary<int, SortedSet<(long DueAt, long MissionId, int Wave)>> _systemMissionReinforcements = [];
    private long _earliestMissionReinforcement = long.MaxValue;

    internal int[] SystemsWithDueMissionReinforcements(long now)
    {
        using var scope = _owner.EnterShared("mission reinforcement systems");
        EnsureLoaded();
        return _earliestMissionReinforcement <= now
            ? _missionReinforcements.TakeWhile(row => row.DueAt <= now).Select(row => row.SystemId).Distinct().ToArray()
            : [];
    }

    internal (long MissionId, int Wave)[] DueMissionReinforcements(int system, long now)
    {
        if (IsLoaded && Volatile.Read(ref _earliestMissionReinforcement) > now)
        {
            return [];
        }
        using var scope = _owner.EnterShared("mission reinforcement deadlines");
        EnsureLoaded();
        return _systemMissionReinforcements.TryGetValue(system, out var rows) && rows.Min.DueAt <= now
            ? rows.TakeWhile(row => row.DueAt <= now).Select(row => (row.MissionId, row.Wave)).ToArray()
            : [];
    }

    private void MissionReinforcementChanged(MissionReinforcementRow? before, MissionReinforcementRow? after)
    {
        if (before is not null)
        {
            _missionReinforcements.Remove((before.DueAt, before.SystemId, before.MissionId, before.Wave));
            var system = _systemMissionReinforcements[before.SystemId];
            system.Remove((before.DueAt, before.MissionId, before.Wave));
            if (system.Count == 0)
            {
                _systemMissionReinforcements.Remove(before.SystemId);
            }
        }
        if (after is not null)
        {
            _missionReinforcements.Add((after.DueAt, after.SystemId, after.MissionId, after.Wave));
            if (!_systemMissionReinforcements.TryGetValue(after.SystemId, out var system))
            {
                _systemMissionReinforcements.Add(after.SystemId, system = []);
            }
            system.Add((after.DueAt, after.MissionId, after.Wave));
        }
        Volatile.Write(
            ref _earliestMissionReinforcement,
            _missionReinforcements.Count == 0 ? long.MaxValue : _missionReinforcements.Min.DueAt
        );
    }

    private void RegisterMissionReinforcements()
    {
        Register(
            new EconomyTable<SavedMissionReinforcement, MissionReinforcementRow>(
                "WorldMissionReinforcement",
                row => new(row.MissionId, row.Wave),
                row => new(row.MissionId, row.Wave, row.SystemId, row.DueAt),
                state => new SavedMissionReinforcement
                {
                    MissionId = state.MissionId,
                    Wave = state.Wave,
                    SystemId = state.SystemId,
                    DueAt = state.DueAt,
                },
                ProjectMissionReinforcements,
                "MissionId,Wave,SystemId,DueAt",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4)),
                state => new(state.MissionId, state.Wave),
                indexes: [new("MissionId", row => row.MissionId)],
                changed: MissionReinforcementChanged,
                primaryColumn: null
            )
        );
    }

    private static void ProjectMissionReinforcements(WorldDbContext world, IReadOnlyList<EconomyChange<MissionReinforcementRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key).ToArray();
            if (removed.Length > 0)
            {
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldMissionReinforcement
                    WHERE (MissionId,Wave) IN (
                        SELECT value->>'$.Id',value->>'$.Part' FROM json_each({JsonSerializer.Serialize(removed)})
                    )
                    """
                );
            }
            var added = batch.Where(change => change.Value is not null).Select(change => change.Value).ToArray();
            if (added.Length > 0)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldMissionReinforcement(MissionId,Wave,SystemId,DueAt)
                    SELECT value->>'$.MissionId',value->>'$.Wave',value->>'$.SystemId',value->>'$.DueAt'
                    FROM json_each({JsonSerializer.Serialize(added)}) WHERE 1
                    ON CONFLICT(MissionId,Wave) DO UPDATE SET SystemId=excluded.SystemId,DueAt=excluded.DueAt
                    """
                );
            }
        }
    }
}
