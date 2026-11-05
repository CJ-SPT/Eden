using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal readonly record struct CombatRowChange(string Table, long Id);

internal sealed partial class LiveCombatRuntime(LiveWorld owner, WorldDatabase database)
{
    private const int ProbabilityResolution = 1_000_000;
    private const int RetainedShips = 512;
    private const int RetainedNpcs = 8192;
    private const int RetainedSystems = 256;
    private const int RetainedItems = RetainedShips * 128;
    private readonly Dictionary<long, LiveCombatShip> _ships = [];
    private readonly Dictionary<long, LiveCombatShip> _savedShips = [];
    private readonly Dictionary<long, LiveCombatNpc> _npcs = [];
    private readonly Dictionary<long, LiveCombatNpc> _savedNpcs = [];
    private readonly HashSet<long> _dirtyShips = [];
    private readonly HashSet<long> _dirtyNpcs = [];
    private readonly HashSet<long> _staleShips = [];
    private readonly HashSet<long> _staleNpcs = [];
    private readonly Dictionary<long, long> _itemPilots = [];
    private readonly Dictionary<long, long> _shipPilots = [];
    private int _internal;
    private Dictionary<long, LiveAggression>? _aggressions;
    private readonly HashSet<long> _dirtyAggressions = [];
    private readonly Dictionary<(int System, long Encounter), long[]> _guardGroups = [];
    internal bool CanRun => _internal == 0 && owner.SqlConnections == 0;

    internal static LiveCombatRuntime? Find(string path) => LiveWorld.Find(path)?.Combat;

    internal LiveCombatShip CurrentShip(long pilotId) => _ships[pilotId];

    internal CombatVolley? Fire(
        long pilotId,
        long moduleId,
        long targetId,
        SpacePosition velocity,
        long now,
        double roll,
        WeaponActivation? activation,
        double? crystalRoll,
        bool banked,
        Func<LiveVolley, CombatVolley> settleFatal
    )
    {
        using var ownership = owner.Enter();
        if (!CanRun)
        {
            return null;
        }
        Trim();
        using var phase = SessionPerformance.Phase("combat.objects");
        if (!double.IsFinite(roll) || roll is < 0 or >= 1)
        {
            throw new InvalidDataException("Invalid weapon accuracy draw.");
        }
        var ship = Ship(pilotId, now);
        var target = Npc(targetId, now);
        if (target is null)
        {
            return null;
        }
        int count = banked ? ship.Banks.Values.FirstOrDefault(b => b.Contains(moduleId))?.Count ?? 1 : 1;
        var draws = Enumerable.Range(0, count).Select(index => new TurretDraw(index == 0 ? roll : Draw(), crystalRoll ?? Draw())).ToArray();
        var plan = LiveCombatRules.Fire(ship, target, moduleId, banked, velocity, now, draws, activation);
        if (plan.Volley.Shot.Destroyed)
        {
            Flush();
            _internal++;
            try
            {
                var result = settleFatal(plan);
                _ships.Remove(pilotId);
                _savedShips.Remove(pilotId);
                _npcs.Remove(targetId);
                _savedNpcs.Remove(targetId);
                _aggressions = null;
                _guardGroups.Clear();
                _contactEpoch++;
                return result;
            }
            finally
            {
                _internal--;
            }
        }
        Hostility(target, ship.ShipId, now);
        _ships[pilotId] = plan.Ship;
        _npcs[targetId] = plan.Target;
        _dirtyShips.Add(pilotId);
        _dirtyNpcs.Add(targetId);
        owner.ObjectsChanged();
        return plan.Volley;
    }

    private static double Draw() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution;

    private void Trim()
    {
        // Evict only at operation boundaries, after projecting every outstanding payment.
        // Revisited pilots and systems hydrate again; historical activity cannot grow forever.
        if (
            _shipPilots.Count >= RetainedShips
            || _npcs.Count >= RetainedNpcs
            || _staleNpcs.Count >= RetainedNpcs
            || _contacts.Count >= RetainedSystems
            || _guardGroups.Count >= RetainedNpcs
            || _itemPilots.Count >= RetainedItems
        )
        {
            Clear();
        }
    }

    private Dictionary<long, LiveAggression> Aggression()
    {
        if (_aggressions is null)
        {
            _aggressions = Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                return world
                    .Set<NpcAggression>()
                    .AsNoTracking()
                    .ToArray()
                    .ToDictionary(
                        a => a.NpcId,
                        a => new LiveAggression(a.NpcId, a.TargetShipId, a.ReadyAt, a.MissileReadyAt, a.LastShotAt)
                    );
            });
        }
        return _aggressions;
    }

    private void Hostility(LiveCombatNpc attacked, long shipId, long now)
    {
        long[] members;
        if (attacked.Role == NpcRole.Pirate || attacked.EncounterId == 0)
        {
            members = [attacked.Id];
        }
        else if (!_guardGroups.TryGetValue((attacked.SystemId, attacked.EncounterId), out members!))
        {
            members = Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                return world
                    .Set<WorldNpc>()
                    .Where(n =>
                        n.SystemId == attacked.SystemId
                        && n.EncounterId == attacked.EncounterId
                        && n.Role != NpcRole.Pirate
                        && n.DestroyedAt == null
                        && n.RetiredAt == null
                    )
                    .Select(n => n.Id)
                    .ToArray();
            });
            _guardGroups[(attacked.SystemId, attacked.EncounterId)] = members;
        }
        var aggression = Aggression();
        List<LiveAggression> additions = [];
        foreach (long id in members)
        {
            if (aggression.ContainsKey(id))
            {
                continue;
            }
            var member = Npc(id, now);
            if (member is null || !member.Available)
            {
                continue;
            }
            if (!double.IsFinite(member.ReactionMilliseconds) || member.ReactionMilliseconds < 0)
            {
                throw new InvalidDataException("Invalid NPC reaction delay.");
            }
            long ready = checked(now + (long)Math.Ceiling(member.ReactionMilliseconds * TimeSpan.TicksPerMillisecond));
            additions.Add(new(id, shipId, ready, ready, null));
        }
        foreach (var addition in additions)
        {
            aggression[addition.NpcId] = addition;
            _dirtyAggressions.Add(addition.NpcId);
        }
    }

    internal IReadOnlyList<long>? Aggressors(long shipId)
    {
        using var ownership = owner.Enter();
        if (CanRun)
        {
            Trim();
        }
        return CanRun ? Aggression().Values.Where(a => a.ShipId == shipId).OrderBy(a => a.NpcId).Select(a => a.NpcId).ToArray() : null;
    }

    internal ShipHealth? Health(long pilotId, long now)
    {
        using var ownership = owner.Enter();
        if (!CanRun)
        {
            return null;
        }
        Trim();
        var ship = Ship(pilotId, now);
        var settled = ship.SettleRepairs(now);
        if (!ReferenceEquals(ship, settled))
        {
            _ships[pilotId] = settled;
            _dirtyShips.Add(pilotId);
            owner.ObjectsChanged();
        }
        return new(settled.ShipId, settled.Health.Defense, settled.Health.At(now));
    }

    private LiveCombatShip Ship(long pilotId, long now)
    {
        if (!_ships.TryGetValue(pilotId, out var ship))
        {
            ship = Loading(() => LoadShip(pilotId, now));
            _ships[pilotId] = ship;
            _savedShips[pilotId] = ship;
            _staleShips.Remove(pilotId);
        }
        else if (_staleShips.Contains(pilotId))
        {
            ship = Loading(() => ReloadShip(ship, now));
            _ships[pilotId] = ship;
            _savedShips[pilotId] = ship;
            _staleShips.Remove(pilotId);
        }
        return ship;
    }

    private LiveCombatNpc? Npc(long id, long now)
    {
        if (!_npcs.TryGetValue(id, out var npc) || _staleNpcs.Contains(id))
        {
            npc = Loading(() => LoadNpc(id, now));
            if (npc is not null)
            {
                _npcs[id] = npc;
                _savedNpcs[id] = npc;
            }
            else
            {
                _npcs.Remove(id);
                _savedNpcs.Remove(id);
            }
            _staleNpcs.Remove(id);
        }
        return npc;
    }

    private T Loading<T>(Func<T> load)
    {
        _internal++;
        try
        {
            return load();
        }
        finally
        {
            _internal--;
        }
    }

    internal void BeforeSql(string sql)
    {
        if (_internal != 0 || _dirtyShips.Count + _dirtyNpcs.Count + _dirtyAggressions.Count == 0)
        {
            return;
        }
        bool read = sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
        string[] combatTables =
        [
            "WorldItem",
            "WorldPosition",
            "WorldShipEnergy",
            "WorldShipHealth",
            "WorldWeaponCooldown",
            "WorldArmorRepair",
            "WorldNpc",
            "WorldCombatDamage",
        ];
        if (!read || combatTables.Any(table => sql.Contains(table, StringComparison.Ordinal)))
        {
            Flush();
        }
    }

    internal void Flush()
    {
        if (_internal != 0 || _dirtyShips.Count + _dirtyNpcs.Count + _dirtyAggressions.Count == 0)
        {
            return;
        }
        using var phase = SessionPerformance.Phase("combat.project");
        _internal++;
        try
        {
            using WorldDbContext world = new(database.Path);
            using var transaction = world.Database.BeginTransaction();
            foreach (long id in _dirtyShips)
            {
                ProjectShip(world, _savedShips[id], _ships[id]);
            }
            foreach (long id in _dirtyNpcs)
            {
                ProjectNpc(world, _savedNpcs[id], _npcs[id]);
            }
            foreach (long id in _dirtyAggressions)
            {
                var aggression = _aggressions![id];
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldNpcAggression(NpcId,TargetShipId,ReadyAt,MissileReadyAt,LastShotAt)
                    VALUES({id},{aggression.ShipId},{aggression.ReadyAt},{aggression.MissileReadyAt},{aggression.LastShotAt})
                    ON CONFLICT(NpcId) DO UPDATE SET TargetShipId=excluded.TargetShipId,ReadyAt=excluded.ReadyAt,
                        MissileReadyAt=excluded.MissileReadyAt,LastShotAt=excluded.LastShotAt
                    """
                );
            }
            transaction.Commit();
            foreach (long id in _dirtyShips)
            {
                _savedShips[id] = _ships[id];
            }
            foreach (long id in _dirtyNpcs)
            {
                _savedNpcs[id] = _npcs[id];
            }
            _dirtyShips.Clear();
            _dirtyNpcs.Clear();
            _dirtyAggressions.Clear();
        }
        finally
        {
            _internal--;
        }
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (_internal != 0)
        {
            return;
        }
        foreach (var change in changes)
        {
            switch (change.Table)
            {
                case "WorldFitRevision":
                    _ships.Remove(change.Id);
                    _savedShips.Remove(change.Id);
                    break;
                case "WorldPosition":
                    _acquisitions.Clear();
                    goto case "WorldShipHealth";
                case "WorldShipHealth":
                case "WorldShipEnergy":
                    if (_shipPilots.TryGetValue(change.Id, out long pilot))
                    {
                        _staleShips.Add(pilot);
                    }
                    break;
                case "WorldWeaponCooldown":
                case "WorldArmorRepair":
                    if (_itemPilots.TryGetValue(change.Id, out long itemPilot))
                    {
                        _staleShips.Add(itemPilot);
                    }
                    break;
                case "WorldItem":
                    // INSERT OR REPLACE can omit SQLite's old-row delete callback.
                    // Invalidate the previous mapped owner's profile on the new row too.
                    if (_itemPilots.TryGetValue(change.Id, out long previousPilot))
                    {
                        _ships.Remove(previousPilot);
                        _savedShips.Remove(previousPilot);
                    }
                    break;
                case "WorldWeaponLink":
                    if (_itemPilots.TryGetValue(change.Id, out long groupedPilot))
                    {
                        _ships.Remove(groupedPilot);
                        _savedShips.Remove(groupedPilot);
                    }
                    break;
                case "WorldNpc":
                    _contactEpoch++;
                    _guardGroups.Clear();
                    _staleNpcs.Add(change.Id);
                    break;
                case "WorldNpcAggression":
                    _contactEpoch++;
                    _aggressions = null;
                    break;
                case "WorldTackle":
                    _contactEpoch++;
                    break;
                case "WorldCombatDamage":
                    _staleNpcs.UnionWith(_npcs.Keys);
                    _staleShips.UnionWith(_ships.Keys);
                    break;
            }
        }
    }

    internal void Clear()
    {
        Flush();
        _ships.Clear();
        _savedShips.Clear();
        _npcs.Clear();
        _savedNpcs.Clear();
        _staleShips.Clear();
        _staleNpcs.Clear();
        _itemPilots.Clear();
        _shipPilots.Clear();
        _aggressions = null;
        _guardGroups.Clear();
        _contacts.Clear();
        _acquisitions.Clear();
    }
}
