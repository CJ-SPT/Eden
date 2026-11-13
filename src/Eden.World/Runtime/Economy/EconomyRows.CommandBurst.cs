using System.Collections.Concurrent;
using System.Text.Json;
using Eden.Data.World.Fleets;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record CommandBurstRow(long Id, long ShipId, int SystemId, long ExpiresAt, string BoostsJson);

    private readonly SortedSet<(long Expiry, long Id)> _allCommandBurstDeadlines = [];
    private readonly ConcurrentDictionary<int, SortedSet<(long Expiry, long Id)>> _commandBurstDeadlines = new();

    private void RegisterCommandBursts()
    {
        Register(
            new EconomyTable<WorldCommandBurst, CommandBurstRow>(
                "WorldCommandBurst",
                row => new(row.Id),
                row => new(row.Id, row.ShipId, row.SystemId, row.ExpiresAt, row.BoostsJson),
                state => new WorldCommandBurst
                {
                    Id = state.Id,
                    ShipId = state.ShipId,
                    SystemId = state.SystemId,
                    ExpiresAt = state.ExpiresAt,
                    BoostsJson = state.BoostsJson,
                },
                ProjectCommandBursts,
                "Id,ShipId,SystemId,ExpiresAt,BoostsJson",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetString(5)),
                state => new(state.Id),
                changed: CommandBurstChanged,
                indexes: [new("ShipId", row => row.ShipId), new("SystemId", row => row.SystemId)],
                rowIdIsKey: true
            )
        );
    }

    private void CommandBurstChanged(CommandBurstRow? before, CommandBurstRow? after)
    {
        if (before is not null && _commandBurstDeadlines.TryGetValue(before.SystemId, out var previous))
        {
            _allCommandBurstDeadlines.Remove((before.ExpiresAt, before.Id));
            previous.Remove((before.ExpiresAt, before.Id));
            if (previous.Count == 0)
            {
                _commandBurstDeadlines.TryRemove(before.SystemId, out _);
            }
        }
        if (after is not null)
        {
            _allCommandBurstDeadlines.Add((after.ExpiresAt, after.Id));
            _commandBurstDeadlines.GetOrAdd(after.SystemId, static _ => []).Add((after.ExpiresAt, after.Id));
        }
    }

    private void ClearCommandBursts()
    {
        _allCommandBurstDeadlines.Clear();
        _commandBurstDeadlines.Clear();
    }

    internal long[] DueCommandBursts(int? system, long now)
    {
        if (system is int id)
        {
            return _commandBurstDeadlines.TryGetValue(id, out var rows) ? Due(rows, now).ToArray() : [];
        }
        return Due(_allCommandBurstDeadlines, now).ToArray();
    }

    private static IEnumerable<long> Due(SortedSet<(long Expiry, long Id)> deadline, long now) =>
        deadline.Count > 0 && deadline.Min.Expiry <= now
            ? deadline.GetViewBetween((0, long.MinValue), (now, long.MaxValue)).Select(row => row.Id)
            : [];

    private static void ProjectCommandBursts(WorldDbContext world, IReadOnlyList<EconomyChange<CommandBurstRow>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key.Id).ToArray();
            if (removed.Length > 0)
            {
                string ids = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql($"DELETE FROM WorldCommandBurst WHERE Id IN (SELECT value FROM json_each({ids}))");
            }
            var added = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (added.Length > 0)
            {
                string rows = JsonSerializer.Serialize(added);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldCommandBurst(Id,ShipId,SystemId,ExpiresAt,BoostsJson)
                    SELECT json_extract(value,'$.Id'),json_extract(value,'$.ShipId'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.ExpiresAt'),json_extract(value,'$.BoostsJson')
                    FROM json_each({rows}) WHERE 1 ON CONFLICT(Id) DO NOTHING
                    """
                );
            }
        }
    }
}
