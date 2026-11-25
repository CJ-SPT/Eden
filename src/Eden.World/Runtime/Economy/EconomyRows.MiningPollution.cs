using System.Collections.Concurrent;
using Eden.Data.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record MiningPollutionRow(
        long Id,
        long AsteroidId,
        int SystemId,
        double X,
        double Y,
        double Z,
        long StartedAt,
        long NextPulseAt,
        long ExpiresAt
    );

    private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, long>> _miningPollutionDeadlines = new();

    internal bool HasMiningPollution(int system) => _miningPollutionDeadlines.TryGetValue(system, out var deadlines) && !deadlines.IsEmpty;

    internal int[] DueMiningPollutionSystems(long now) =>
        _miningPollutionDeadlines.IsEmpty
            ? []
            : _miningPollutionDeadlines
                .Where(system => system.Value.Any(cloud => cloud.Value <= now))
                .Select(system => system.Key)
                .Order()
                .ToArray();

    private void MiningPollutionChanged(MiningPollutionRow? before, MiningPollutionRow? after)
    {
        if (before is not null && after is null && _miningPollutionDeadlines.TryGetValue(before.SystemId, out var deadlines))
        {
            deadlines.TryRemove(before.Id, out _);
            if (deadlines.IsEmpty)
            {
                _miningPollutionDeadlines.TryRemove(KeyValuePair.Create(before.SystemId, deadlines));
            }
        }
        if (after is not null)
        {
            _miningPollutionDeadlines.GetOrAdd(after.SystemId, _ => new())[after.Id] = Math.Min(after.NextPulseAt, after.ExpiresAt);
        }
    }

    private void RegisterMiningPollutionClouds()
    {
        Register(
            new EconomyTable<SavedMiningPollutionCloud, MiningPollutionRow>(
                "SavedMiningPollutionCloud",
                row => new(row.Id),
                row => new(row.Id, row.AsteroidId, row.SystemId, row.X, row.Y, row.Z, row.StartedAt, row.NextPulseAt, row.ExpiresAt),
                row =>
                    new()
                    {
                        Id = row.Id,
                        AsteroidId = row.AsteroidId,
                        SystemId = row.SystemId,
                        X = row.X,
                        Y = row.Y,
                        Z = row.Z,
                        StartedAt = row.StartedAt,
                        NextPulseAt = row.NextPulseAt,
                        ExpiresAt = row.ExpiresAt,
                    },
                ProjectMiningPollution,
                "Id,AsteroidId,SystemId,X,Y,Z,StartedAt,NextPulseAt,ExpiresAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetDouble(4),
                        reader.GetDouble(5),
                        reader.GetDouble(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9)
                    ),
                row => new(row.Id),
                changed: MiningPollutionChanged,
                indexes: [new("SystemId", row => row.SystemId), new("AsteroidId", row => row.AsteroidId)],
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectMiningPollution(WorldDbContext world, IReadOnlyList<EconomyChange<MiningPollutionRow>> changes)
    {
        foreach (var deleted in changes.Where(change => change.Value is null))
        {
            world.Database.ExecuteSql($"DELETE FROM SavedMiningPollutionCloud WHERE Id={deleted.Key.Id}");
        }
        foreach (var row in changes.Select(change => change.Value).OfType<MiningPollutionRow>())
        {
            world.Database.ExecuteSql(
                $"""
                INSERT INTO SavedMiningPollutionCloud(Id,AsteroidId,SystemId,X,Y,Z,StartedAt,NextPulseAt,ExpiresAt)
                VALUES({row.Id},{row.AsteroidId},{row.SystemId},{row.X},{row.Y},{row.Z},{row.StartedAt},{row.NextPulseAt},{row.ExpiresAt})
                ON CONFLICT(Id) DO UPDATE SET NextPulseAt=excluded.NextPulseAt
                """
            );
        }
    }
}
