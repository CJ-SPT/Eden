using System.Linq.Expressions;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.World.Fittings;
using Eden.World.Market;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

// The same typed operations run on private resident views or an existing offline
// SQL transaction. Neither adapter starts a second transaction inside an action.
internal abstract class WorldAction
{
    private readonly List<Action> _committed = [];

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
    internal abstract bool HasPilotName(string name);
    internal abstract bool TrafficCharacterTaken(long character);
    internal abstract bool Prepared(InstitutionalPreparation value);
    internal abstract void Remember(InstitutionalPreparation value);
    internal abstract WorldSettings Settings { get; }
    internal abstract string WorldPath { get; }
    internal abstract long ActiveTicks { get; }
    internal abstract long NextExpiry { get; }
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
