using System.Collections.Concurrent;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Propulsion;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private readonly ConcurrentDictionary<long, byte> _pilotIdentities = new();
    private readonly ConcurrentDictionary<int, int> _pilotMissileSystems = new();
    private readonly ConcurrentDictionary<long, int> _npcMissilePilots = new();
    private readonly ConcurrentDictionary<long, int> _missileReportPilots = new();
    private readonly ConcurrentDictionary<long, byte> _missileImpacts = new();
    private readonly SortedSet<(long EndedAt, long Id)> _npcMissileEnds = [];
    private readonly SortedSet<(long EndedAt, long Id)> _reportedMissileEnds = [];
    private long _oldestNpcMissileEnd = long.MaxValue;
    private readonly ConcurrentDictionary<long, long> _npcEwarTargets = new();
    private readonly ConcurrentDictionary<long, long> _npcEwarSources = new();
    private readonly ConcurrentDictionary<long, long> _tackleTargets = new();
    private readonly ConcurrentDictionary<long, long> _playerEwarTargets = new();
    private long _effectRevision;
    private int _tackleRows;
    private int _playerEwarRows;

    private void ClearCombatIndexes()
    {
        _pilotIdentities.Clear();
        _pilotMissileSystems.Clear();
        _npcMissilePilots.Clear();
        _missileReportPilots.Clear();
        _missileImpacts.Clear();
        _npcMissileEnds.Clear();
        _reportedMissileEnds.Clear();
        Volatile.Write(ref _oldestNpcMissileEnd, long.MaxValue);
        _npcEwarTargets.Clear();
        _npcEwarSources.Clear();
        _tackleTargets.Clear();
        _playerEwarTargets.Clear();
        _effectRevision = 0;
        _tackleRows = 0;
        _playerEwarRows = 0;
    }

    private void PilotIdentityChanged(EconomyPilot? before, EconomyPilot? after)
    {
        if (after is not null)
        {
            _pilotIdentities.TryAdd(after.Id, 0);
        }
        else if (before is not null)
        {
            _pilotIdentities.TryRemove(before.Id, out _);
        }
    }

    internal bool HasPilotMissiles(int system) => _pilotMissileSystems.ContainsKey(system);

    internal bool HasShipLoss(long ship) => _tables[typeof(ShipLoss)].Contains(new(ship));

    internal bool HasMissileReports(long pilot) => _missileReportPilots.ContainsKey(pilot);

    internal bool MissileImpacted(long id) => _missileImpacts.ContainsKey(id);

    internal bool HasNpcMissileWork(long pilot, long oldest) =>
        _npcMissilePilots.ContainsKey(pilot) || Volatile.Read(ref _oldestNpcMissileEnd) < oldest;

    internal IEnumerable<long> ExpiredMissileHistory(long oldest, bool npcOnly) =>
        (npcOnly ? _npcMissileEnds : _reportedMissileEnds).TakeWhile(row => row.EndedAt < oldest).Select(row => row.Id);

    internal bool HasTackleRows => Volatile.Read(ref _tackleRows) != 0;
    internal bool HasPlayerEwarRows => Volatile.Read(ref _playerEwarRows) != 0;

    internal long NpcEwarTargetVersion(long ship) => _npcEwarTargets.GetValueOrDefault(ship);

    internal long NpcEwarSourceVersion(long npc) => _npcEwarSources.GetValueOrDefault(npc);

    internal long TackleTargetVersion(long ship) => _tackleTargets.GetValueOrDefault(ship);

    internal long PlayerEwarTargetVersion(long target) => _playerEwarTargets.GetValueOrDefault(target);

    private static void AdjustCount<TKey>(ConcurrentDictionary<TKey, int> counts, TKey key, bool add)
        where TKey : notnull
    {
        if (add)
        {
            counts.AddOrUpdate(key, 1, (_, count) => checked(count + 1));
        }
        else if (counts.TryGetValue(key, out int count))
        {
            if (count == 1)
            {
                counts.TryRemove(key, out _);
            }
            else
            {
                counts[key] = count - 1;
            }
        }
    }

    private void MissileChanged(EconomyWorldMissile? before, EconomyWorldMissile? after)
    {
        if (
            before is not null
            && after is not null
            && before.SourceKind == after.SourceKind
            && before.SystemId == after.SystemId
            && before.PilotId == after.PilotId
            && before.Status == after.Status
            && (before.Outcome is not null && !before.Reported) == (after.Outcome is not null && !after.Reported)
            && before.EndedAt == after.EndedAt
        )
        {
            return;
        }
        void Update(EconomyWorldMissile row, bool add)
        {
            if (row.Status == MissileFlightStatus.Flying)
            {
                if (row.SourceKind == MissileSourceKind.Pilot)
                {
                    AdjustCount(_pilotMissileSystems, row.SystemId, add);
                }
                else if (row.SourceKind == MissileSourceKind.Npc)
                {
                    AdjustCount(_npcMissilePilots, row.PilotId, add);
                }
            }
            if (row.SourceKind == MissileSourceKind.Pilot && row.Outcome is not null && !row.Reported)
            {
                AdjustCount(_missileReportPilots, row.PilotId, add);
            }
            if (row.Status == MissileFlightStatus.Impact)
            {
                if (add)
                {
                    _missileImpacts[row.Id] = 0;
                }
                else
                {
                    _missileImpacts.TryRemove(row.Id, out _);
                }
            }
            if (row.EndedAt is not long ended)
            {
                return;
            }
            if (row.SourceKind == MissileSourceKind.Npc)
            {
                UpdateEnd(_npcMissileEnds, ended, row.Id, add);
            }
            if (row.Outcome is null || row.Reported)
            {
                UpdateEnd(_reportedMissileEnds, ended, row.Id, add);
            }
        }
        if (before is not null)
        {
            Update(before, false);
        }
        if (after is not null)
        {
            Update(after, true);
        }
        Volatile.Write(ref _oldestNpcMissileEnd, _npcMissileEnds.Count == 0 ? long.MaxValue : _npcMissileEnds.Min.EndedAt);
    }

    private static void UpdateEnd(SortedSet<(long EndedAt, long Id)> ends, long ended, long id, bool add)
    {
        if (add)
        {
            ends.Add((ended, id));
        }
        else
        {
            ends.Remove((ended, id));
        }
    }

    private static void EffectVersion(ConcurrentDictionary<long, long> versions, long key, bool present, long revision)
    {
        if (present)
        {
            versions[key] = revision;
        }
        else
        {
            versions.TryRemove(key, out _);
        }
    }

    private void NpcEwarChanged(EconomyWorldNpcEwar? before, EconomyWorldNpcEwar? after)
    {
        if (before == after)
        {
            return;
        }
        long revision = checked(++_effectRevision);
        var table = (EconomyTable<WorldNpcEwar, EconomyWorldNpcEwar>)_tables[typeof(WorldNpcEwar)];
        void Target(long id) =>
            EffectVersion(_npcEwarTargets, id, table.Indexed(nameof(WorldNpcEwar.TargetShipId), id).Count != 0, revision);
        void Source(long id) => EffectVersion(_npcEwarSources, id, table.Indexed(nameof(WorldNpcEwar.NpcId), id).Count != 0, revision);
        if (before is not null && before.TargetShipId != after?.TargetShipId)
        {
            Target(before.TargetShipId);
        }
        if (before is not null && before.NpcId != after?.NpcId)
        {
            Source(before.NpcId);
        }
        if (after is not null)
        {
            Target(after.TargetShipId);
            Source(after.NpcId);
        }
    }

    private void TackleChanged(EconomyWorldTackle? before, EconomyWorldTackle? after)
    {
        if (before == after)
        {
            return;
        }
        if (before is null)
        {
            Interlocked.Increment(ref _tackleRows);
        }
        else if (after is null)
        {
            Interlocked.Decrement(ref _tackleRows);
        }
        long revision = checked(++_effectRevision);
        var table = (EconomyTable<WorldTackle, EconomyWorldTackle>)_tables[typeof(WorldTackle)];
        void Target(long id) =>
            EffectVersion(
                _tackleTargets,
                id,
                table.Indexed(nameof(WorldTackle.TargetId), id).Any(key => table.Snapshot(key)?.TargetKind == TackleTargetKind.Ship),
                revision
            );
        if (
            before?.TargetKind == TackleTargetKind.Ship
            && (before.TargetId != after?.TargetId || after?.TargetKind != TackleTargetKind.Ship)
        )
        {
            Target(before.TargetId);
        }
        if (after?.TargetKind == TackleTargetKind.Ship)
        {
            Target(after.TargetId);
        }
    }

    private void PlayerEwarChanged(EconomyWorldPlayerEwar? before, EconomyWorldPlayerEwar? after)
    {
        if (before == after)
        {
            return;
        }
        if (before is null)
        {
            Interlocked.Increment(ref _playerEwarRows);
        }
        else if (after is null)
        {
            Interlocked.Decrement(ref _playerEwarRows);
        }
        long revision = checked(++_effectRevision);
        var table = (EconomyTable<WorldPlayerEwar, EconomyWorldPlayerEwar>)_tables[typeof(WorldPlayerEwar)];
        void Target(long id) =>
            EffectVersion(_playerEwarTargets, id, table.Indexed(nameof(WorldPlayerEwar.TargetId), id).Count != 0, revision);
        if (before is not null && before.TargetId != after?.TargetId)
        {
            Target(before.TargetId);
        }
        if (after is not null)
        {
            Target(after.TargetId);
        }
    }
}
