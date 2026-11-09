using System.Runtime.CompilerServices;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Loot;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed record TractorPosition(long Id, SpacePosition Position);

internal sealed record TractorCheckpoint(IReadOnlyList<TractorPosition> Positions)
{
    internal void Apply(WorldDbContext world)
    {
        foreach (var value in Positions)
        {
            world.Database.ExecuteSql(
                $"UPDATE WorldWreck SET X={value.Position.X},Y={value.Position.Y},Z={value.Position.Z} WHERE Id={value.Id}"
            );
        }
    }
}

internal sealed class LiveTractors(LiveWorld owner, WorldDatabase database)
{
    private sealed record Tractor(InformationCycle Cycle, SpacePosition Source, double Radii, long AdvancedAt, ICooperativeFleets? Fleets);

    private readonly Dictionary<long, Tractor> _cycles = [];
    private readonly Dictionary<long, SpacePosition> _positions = [];
    private readonly HashSet<long> _dirty = [];
    private bool _writing;
    private long _revision;

    private sealed record View(long Revision, IReadOnlyList<WreckContact> Contacts);

    private readonly ConditionalWeakTable<IReadOnlyList<WreckContact>, View> _views = new();

    internal SpacePosition? Position(long id) => _positions.GetValueOrDefault(id);

    internal void Start(InformationCycle cycle, SpacePosition source, SpacePosition wreck, double radii, ICooperativeFleets? fleets)
    {
        using var ownership = owner.Enter();
        _cycles[cycle.Module.ItemId] = new(cycle, source, radii, cycle.StartedAt, fleets);
        _positions[cycle.TargetId] = wreck;
        _revision++;
    }

    internal bool Has(InformationCycle cycle)
    {
        using var ownership = owner.Enter();
        return _cycles.TryGetValue(cycle.Module.ItemId, out var current) && current.Cycle.ReadyAt == cycle.ReadyAt;
    }

    internal void Advance(long now)
    {
        using var ownership = owner.Enter();
        foreach (var (module, tractor) in _cycles.ToArray())
        {
            bool allowed =
                tractor.Cycle.TargetOwnerId == tractor.Cycle.PilotId
                || tractor.Fleets?.Identity(tractor.Cycle.PilotId) is { } sourceFleet
                    && tractor.Fleets.Identity(tractor.Cycle.TargetOwnerId ?? 0)?.FleetId == sourceFleet.FleetId;
            if (!allowed)
            {
                _cycles.Remove(module);
                continue;
            }
            long until = Math.Min(now, tractor.Cycle.ReadyAt);
            if (until <= tractor.AdvancedAt)
            {
                continue;
            }
            var source = owner.Combat?.InformationPosition(tractor.Cycle.ShipId) ?? tractor.Source;
            var previous = _positions[tractor.Cycle.TargetId];
            if (source.DistanceTo(previous) > tractor.Cycle.Module.Range + tractor.Radii)
            {
                _cycles.Remove(module);
                continue;
            }
            var position = InformationRules.Pull(
                previous,
                source,
                InformationRules.TractorStopDistance + tractor.Radii,
                tractor.Cycle.Module.TractorVelocity,
                (until - tractor.AdvancedAt) / (double)TimeSpan.TicksPerSecond
            );
            _cycles[module] = tractor with { AdvancedAt = until, Source = source };
            if (position != previous)
            {
                _positions[tractor.Cycle.TargetId] = position;
                _dirty.Add(tractor.Cycle.TargetId);
                _revision++;
                owner.ObjectsChanged();
            }
        }
    }

    internal IReadOnlyList<WreckContact> Contacts(IReadOnlyList<WreckContact> contacts, long now)
    {
        using var ownership = owner.Enter();
        Advance(now);
        if (_positions.Count == 0)
        {
            return contacts;
        }
        if (_views.TryGetValue(contacts, out var view) && view.Revision == _revision)
        {
            return view.Contacts;
        }
        var current = contacts
            .Select(w => _positions.TryGetValue(w.Id, out var position) && position != w.Position ? w with { Position = position } : w)
            .ToArray();
        IReadOnlyList<WreckContact> result = current.SequenceEqual(contacts, ReferenceEqualityComparer.Instance) ? contacts : current;
        _views.AddOrUpdate(contacts, new(_revision, result));
        return result;
    }

    internal TractorCheckpoint Capture() => new(_dirty.Select(id => new TractorPosition(id, _positions[id])).ToArray());

    internal void Stop(long module)
    {
        using var ownership = owner.Enter();
        if (_cycles.Remove(module, out var tractor) && !_dirty.Contains(tractor.Cycle.TargetId))
        {
            _positions.Remove(tractor.Cycle.TargetId);
            _revision++;
        }
    }

    internal void Flush()
    {
        if (_writing || _dirty.Count == 0)
        {
            return;
        }
        _writing = true;
        try
        {
            using WorldDbContext world = new(database.Path);
            using var transaction = world.Database.BeginTransaction();
            Capture().Apply(world);
            transaction.Commit();
            _dirty.Clear();
            var active = _cycles.Values.Select(c => c.Cycle.TargetId).ToHashSet();
            foreach (long target in _positions.Keys.Where(id => !active.Contains(id)).ToArray())
            {
                _positions.Remove(target);
                _revision++;
            }
        }
        finally
        {
            _writing = false;
        }
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (_writing)
        {
            return;
        }
        foreach (var key in _cycles.Keys.ToArray())
        {
            var cycle = _cycles[key].Cycle;
            if (
                changes.Any(c =>
                    c.Table == "schema"
                    || c.Table == "WorldItem" && c.Id == key
                    || c.Table is "WorldShip" or "WorldShipLoss" or "WorldPositionMembership" && c.Id == cycle.ShipId
                    || c.Table == "WorldWreck" && c.Id == cycle.TargetId
                )
            )
            {
                _cycles.Remove(key);
            }
        }
        foreach (var change in changes.Where(c => c.Table == "WorldWreck"))
        {
            _positions.Remove(change.Id);
            _dirty.Remove(change.Id);
            _revision++;
        }
    }
}
