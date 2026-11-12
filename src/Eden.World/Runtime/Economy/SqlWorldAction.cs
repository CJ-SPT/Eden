using System.Linq.Expressions;
using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Structures;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed class SqlWorldAction(WorldDbContext world) : WorldAction
{
    internal override int PilotCount => world.Pilots.Count();

    internal override bool HasPilotName(string name) =>
        world.Pilots.Select(p => p.Name).AsEnumerable().Contains(name, StringComparer.OrdinalIgnoreCase);

    internal override bool TrafficCharacterTaken(long character) =>
        world.Set<Eden.Data.World.Capsuleers.SavedTrafficIdentity>().Any(i => i.CharacterId == character);

    private readonly Dictionary<Type, object> _sets = [];
    private readonly Dictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>> _attributes = [];
    internal override WorldSettings Settings => world.Settings;
    internal override string WorldPath => world.WorldPath;
    internal override long ActiveTicks => world.Set<Eden.Data.World.Capsuleers.SavedCapsuleerClock>().Single().ElapsedTicks;
    internal override long NextExpiry =>
        world.Set<SavedPersonalOrder>().Where(o => o.Status == PersonalOrderStatus.Open).Min(o => (long?)o.Expires) ?? long.MaxValue;

    internal override long MarketHead(long pilot) =>
        world.Set<SavedMarketChange>().Where(c => c.PilotId == pilot).Max(c => (long?)c.Id) ?? 0;

    internal override bool Prepared(InstitutionalPreparation value) => WorldConnections.PreparedInstitutional(world, value);

    internal override void Remember(InstitutionalPreparation value) => WorldConnections.RememberInstitutional(world, value);

    internal override EconomyStructure? Structure(long id) =>
        world.Set<SavedStructure>().Find(id) is { } row ? new(row.Id, row.SystemId, row.TypeId, row.State) : null;

    internal override int StructureSystemId(long id) => Structure(id)?.SystemId ?? throw new InvalidDataException("Unknown structure.");

    internal override void RequireUncloaked(long ship) => Eden.World.Propulsion.PilotSpatialModuleStore.RequireUncloaked(world, ship);

    internal override void SetAttributes(long pilot, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        _attributes[pilot] = attributes;

    internal override IReadOnlyDictionary<DogmaAttributeId, double> Attributes(long pilot)
    {
        if (_attributes.TryGetValue(pilot, out var values))
        {
            return values;
        }
        using CatalogDbContext catalog = new(new WorldDatabase(WorldPath).CatalogPath);
        return PilotFittingStore.ShipAttributes(world, catalog, pilot);
    }

    internal override EconomySet<T> Set<T>()
    {
        if (!_sets.TryGetValue(typeof(T), out var rows))
        {
            rows = new SqlActionRows<T>(world);
            _sets.Add(typeof(T), rows);
        }
        return (EconomySet<T>)rows;
    }

    internal override EconomyEntry<T> Add<T>(T row) => new(world.Add(row).Entity, EntityState.Added);

    internal override IQueryable<T> Filter<T>(Expression<Func<T, bool>> predicate) => ((SqlActionRows<T>)Set<T>()).Filter(predicate);

    internal override void Remove<T>(T row) => world.Remove(row);

    internal override void RemoveRange<T>(IEnumerable<T> rows) => world.RemoveRange(rows);

    internal override EconomyEntry<T> Entry<T>(T row) => new(row, world.Entry(row).State);

    internal override void SaveChanges() => world.SaveChanges();

    internal override long Next(string name, long minimum = 0)
    {
        long saved = world.Database.SqlQuery<long>($"SELECT COALESCE(MAX(seq),0) AS Value FROM sqlite_sequence WHERE name={name}").Single();
        return checked(Math.Max(saved, minimum) + 1);
    }

    internal override long NextItemId() => InventoryPersistence.NextId(world);

    internal override string? FitRevision(long pilot) => world.FitRevision(pilot);

    internal override Dictionary<DogmaAttributeId, double>? CachedFit(FitCacheKey key, string? revision) => world.CachedFit(key, revision);

    internal override void StageFit(FitCacheKey key, string? revision, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        world.StageFit(key, revision, attributes);

    internal override FittingSnapshot? CachedFittingSnapshot(long pilot, long ship, string? revision) =>
        world.CachedFittingSnapshot(pilot, ship, revision);

    internal override void StageFittingSnapshot(long pilot, long ship, string? revision, FittingSnapshot snapshot) =>
        world.StageFittingSnapshot(pilot, ship, revision, snapshot);
}

internal sealed class SqlActionRows<T>(WorldDbContext world) : EconomySet<T>
    where T : class
{
    private IQueryable<T> Query =>
        typeof(T) == typeof(Pilot)
            ? (IQueryable<T>)world.Pilots.Include(p => p.Account).Include(p => p.ActiveShip).ThenInclude(s => s.Position)
        : typeof(T) == typeof(Ship) ? (IQueryable<T>)world.Ships.Include(s => s.Position)
        : world.Set<T>();
    protected override IEnumerable<T> Rows => Query.ToArray();

    internal IQueryable<T> Filter(Expression<Func<T, bool>> predicate) => Query.Where(predicate);

    internal override IReadOnlyCollection<T> Local => world.ChangeTracker.Entries<T>().Select(e => e.Entity).ToArray();

    internal override IQueryable<T> Where(Expression<Func<T, bool>> predicate) => SelectRows(predicate).AsQueryable();

    internal override IEnumerable<T> SelectRows(Expression<Func<T, bool>>? predicate) =>
        predicate is null ? Rows : Rows.Where(predicate.Compile(preferInterpretation: true));

    internal override T? Find(EconomyKey id)
    {
        var properties = world.Model.FindEntityType(typeof(T))!.FindPrimaryKey()!.Properties;
        object[] keys = properties
            .Select(
                (property, index) =>
                    property.ClrType == typeof(string) ? (object)id.Name!
                    : property.ClrType.IsEnum ? Enum.ToObject(property.ClrType, index == 0 ? id.Id : id.Part)
                    : property.ClrType == typeof(int) ? (object)checked((int)(index == 0 ? id.Id : id.Part))
                    : index == 0 ? id.Id
                    : id.Part
            )
            .ToArray();
        var row = world.Set<T>().Find(keys);
        if (row is Pilot pilot)
        {
            world.Entry(pilot).Reference(p => p.Account).Load();
            world.Entry(pilot).Reference(p => p.ActiveShip).Load();
            world.Entry(pilot.ActiveShip).Reference(s => s.Position).Load();
        }
        else if (row is Ship ship)
        {
            world.Entry(ship).Reference(s => s.Position).Load();
        }
        return row;
    }

    internal override EconomyEntry<T> Add(T row) => new(world.Add(row).Entity, EntityState.Added);

    internal override void Remove(T row) => world.Remove(row);

    internal override EntityState State(T row) => world.Entry(row).State;

    public override bool Prepare() => throw new NotSupportedException("SQL owns offline validation.");

    public override void Publish() => throw new NotSupportedException("SQL owns offline publication.");

    public override IReadOnlyCollection<Eden.World.Runtime.Combat.CombatRowChange> Changes => [];
    public override IReadOnlyCollection<long> Owners => [];
}
