using System.Linq.Expressions;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mail;
using Eden.Data.World.Mining;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.World.Fittings;
using Eden.World.Market;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

// The same typed operations run on private resident views or an existing offline
// SQL transaction. Neither adapter starts a second transaction inside an action.
internal abstract class WorldAction
{
    private readonly List<Action> _committed = [];
    private readonly Dictionary<int, long> _structureMissileAdvances = [];

    internal virtual bool HasStructureMissiles(int system) => false;

    internal virtual Eden.Gameplay.Targeting.IPilotTargetLocks? TargetLocks => null;

    internal virtual bool TetherProtected(long ship, long now) => false;

    internal virtual IReadOnlyCollection<int> StructureMissileSystems => [];

    internal bool BeginStructureMissileAdvance(int system, long now)
    {
        if (_structureMissileAdvances.TryGetValue(system, out long advanced) && now <= advanced)
        {
            return false;
        }
        _structureMissileAdvances[system] = now;
        return true;
    }

    internal void AfterCommit(Action action) => _committed.Add(action);

    internal void PublishActions()
    {
        foreach (var action in _committed)
        {
            action();
        }
        _committed.Clear();
    }

    internal abstract int PilotCount { get; }

    internal virtual (int OldCount, int NewCount, IReadOnlyList<long> Recipients) MembershipChangeAudience(int previous, int next) =>
        (
            Pilots.Count(p => p.CorporationId == previous),
            Pilots.Count(p => p.CorporationId == next),
            Pilots
                .Where(p => p.CorporationId == previous || p.CorporationId == next)
                .Where(p => p.AccountId > 0)
                .Select(p => p.Id)
                .ToArray()
        );

    internal virtual bool PilotExists(long id) => Filter<Pilot>(p => p.Id == id).Any();

    internal virtual long? SkillPoints(long pilot, int type) => Set<PilotSkill>().Find(pilot, type)?.Points;

    internal abstract bool HasPilotName(string name);
    internal abstract bool TrafficCharacterTaken(long character);
    internal abstract bool Prepared(InstitutionalPreparation value);
    internal abstract void Remember(InstitutionalPreparation value);
    internal abstract WorldSettings Settings { get; }
    internal abstract string WorldPath { get; }
    internal abstract long ActiveTicks { get; }
    internal abstract long NextExpiry { get; }

    internal virtual long MailHead(long pilot) => Filter<SavedMailChange>(c => c.PilotId == pilot).Max(c => (long?)c.Id) ?? 0;

    internal virtual long MailingListHead(long pilot) => Filter<SavedMailingListChange>(c => c.PilotId == pilot).Max(c => (long?)c.Id) ?? 0;

    internal abstract long MarketHead(long pilot);
    internal abstract EconomyStructure? Structure(long id);
    internal abstract int StructureSystemId(long id);
    internal abstract void RequireUncloaked(long ship);
    internal abstract void SetAttributes(long pilot, IReadOnlyDictionary<DogmaAttributeId, double> attributes);
    internal abstract IReadOnlyDictionary<DogmaAttributeId, double> Attributes(long pilot);
    internal abstract EconomySet<T> Set<T>()
        where T : class;

    internal virtual IQueryable<T> Filter<T>(Expression<Func<T, bool>> predicate)
        where T : class => Set<T>().Where(predicate);

    internal virtual OwnedItem[] FittedItems(long owner, long ship) =>
        Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == owner
                && i.LocationId == ship
                && (
                    (i.FlagId >= (int)InventoryFlag.LowSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7)
                    || (i.FlagId >= (int)InventoryFlag.RigSlot0 && i.FlagId <= (int)InventoryFlag.RigSlot2)
                )
            )
            .ToArray();

    internal virtual OwnedItem[] LoadedFittingItems(long owner, long[] modules) =>
        Set<OwnedItem>().Where(i => i.OwnerId == owner && Enumerable.Contains(modules, i.LocationId)).ToArray();

    internal virtual IEnumerable<EconomyWorldAsteroid> AsteroidsInSystem(int system) =>
        Filter<WorldAsteroid>(a => a.SystemId == system)
            .Select(a => new EconomyWorldAsteroid(a.Id, a.BeltId, a.Quantity, a.Radius, a.SystemId, a.TypeId, a.X, a.Y, a.Z));

    internal virtual IEnumerable<int> RenewedBeltsInSystem(int system) =>
        Filter<SavedBeltRenewal>(b => b.SystemId == system).Select(b => b.BeltId);

    internal abstract IReadOnlyList<long> ContainedInventoryIds(long location);

    internal virtual IReadOnlyList<WorldMissile> ExpiredMissileHistory(long oldest, bool npcOnly) =>
        npcOnly
            ? Filter<WorldMissile>(row => row.SourceKind == MissileSourceKind.Npc && row.EndedAt < oldest).ToArray()
            : Filter<WorldMissile>(row => row.EndedAt < oldest && (row.Outcome == null || row.Reported)).ToArray();

    internal abstract EconomyEntry<T> Add<T>(T row)
        where T : class;
    internal abstract void Remove<T>(T row)
        where T : class;
    internal abstract void RemoveRange<T>(IEnumerable<T> rows)
        where T : class;
    internal abstract EconomyEntry<T> Entry<T>(T row)
        where T : class;
    internal abstract void SaveChanges();
    internal abstract long Next(string name, long minimum = 0);
    internal abstract long NextItemId();

    internal long AvailableNpcId(long preferred)
    {
        var npcs = Set<WorldNpc>();
        // Ordinary NPCs share the table's sequence and may have already used an
        // authored wave's preferred ID. Keep the existing NPC and use the sequence.
        long id = preferred;
        while (npcs.Find(id) is not null)
        {
            id = Next(nameof(WorldNpc), id);
        }
        return id;
    }

    internal EconomyTracking ChangeTracker => new(this);
    internal EconomySet<Pilot> Pilots => Set<Pilot>();
    internal EconomySet<Ship> Ships => Set<Ship>();
    internal EconomySet<FlightPosition> Positions => Set<FlightPosition>();

    internal virtual string? FitRevision(long pilot) => null;

    internal virtual Dictionary<DogmaAttributeId, double>? CachedFit(FitCacheKey key, string? revision) => null;

    internal virtual void StageFit(FitCacheKey key, string? revision, IReadOnlyDictionary<DogmaAttributeId, double> attributes) { }

    internal virtual FittingSnapshot? CachedFittingSnapshot(long pilot, long ship, string? revision) => null;

    internal virtual void StageFittingSnapshot(long pilot, long ship, string? revision, FittingSnapshot snapshot) { }
}
