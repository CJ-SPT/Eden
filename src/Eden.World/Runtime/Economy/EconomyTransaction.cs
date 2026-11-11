using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class EconomyTransaction(
    ResidentEconomy store,
    LiveWorld owner,
    WorldDatabase database,
    IDisposable lease,
    IDisposable systems
) : IDisposable
{
    private readonly Dictionary<Type, IEconomySet> _sets = [];
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly Dictionary<int, InstitutionalPreparation> _preparations = [];

    internal bool Prepared(InstitutionalPreparation preparation) =>
        _preparations.GetValueOrDefault(preparation.StationId) == preparation || store.Prepared(preparation);

    internal void Remember(InstitutionalPreparation preparation) => _preparations[preparation.StationId] = preparation;

    internal long NextExpiry => store.NextExpiry;

    internal long MarketHead(long pilot) => store.MarketHead(pilot);

    internal EconomyStructure? Structure(long id) => store.Structure(id);

    internal int StructureSystemId(long id) => store.StructureSystemId(id);

    private readonly Dictionary<long, IReadOnlyDictionary<Eden.Gameplay.Dogma.DogmaAttributeId, double>> _attributes = [];

    internal void SetAttributes(long pilot, IReadOnlyDictionary<Eden.Gameplay.Dogma.DogmaAttributeId, double> attributes) =>
        _attributes[pilot] = attributes;

    internal IReadOnlyDictionary<Eden.Gameplay.Dogma.DogmaAttributeId, double> Attributes(long pilot) =>
        _attributes.TryGetValue(pilot, out var attributes)
            ? attributes
            : throw new InvalidOperationException("Prepare fitting attributes before opening the economic transaction.");

    private bool _completed;
    private bool _disposed;

    internal void RequireUncloaked(long ship)
    {
        if (store.Cloaked(ship))
        {
            throw new InvalidDataException("Decloak before moving inventory, reloading or changing overload settings.");
        }
    }

    internal long ActiveTicks => owner.CapsuleerActivity.Read().ElapsedTicks;
    internal EconomyTracking ChangeTracker => new(this);
    internal int Materializations => _sets.Values.Sum(set => set.Materializations);
    internal WorldSettings Settings => database.Settings;
    internal string WorldPath => database.Path;
    internal EconomySet<Pilot> Pilots => Set<Pilot>();
    internal EconomySet<Ship> Ships => Set<Ship>();
    internal EconomySet<FlightPosition> Positions => Set<FlightPosition>();

    internal EconomySet<T> Set<T>()
        where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The economic view already committed.");
        }
        if (!_sets.TryGetValue(typeof(T), out var rows))
        {
            rows = store.Open<T>(this);
            _sets.Add(typeof(T), rows);
        }
        return (EconomySet<T>)rows;
    }

    internal EconomyEntry<T> Add<T>(T row)
        where T : class
    {
        Set<T>().Add(row);
        return new(row, EntityState.Added);
    }

    internal void Remove<T>(T row)
        where T : class => Set<T>().Remove(row);

    internal void RemoveRange<T>(IEnumerable<T> rows)
        where T : class
    {
        foreach (var row in rows.ToArray())
        {
            Remove(row);
        }
    }

    internal EconomyEntry<T> Entry<T>(T row)
        where T : class => new(row, Set<T>().State(row));

    // A save within an action only prepares its private view. Publication belongs
    // to Commit, so a later rejected batch entry cannot leave an earlier fill paid.
    internal void SaveChanges() { }

    private void Reserve(string name, long id)
    {
        long previous = _sequences.GetValueOrDefault(name, store.Sequence(name));
        if (id > previous)
        {
            _sequences[name] = id;
        }
    }

    internal long Next(string name, long minimum = 0)
    {
        long current = _sequences.TryGetValue(name, out long staged) ? staged : store.Sequence(name);
        long next = checked(Math.Max(current, minimum) + 1);
        _sequences[name] = next;
        return next;
    }

    internal long NextItemId()
    {
        string[] names = ["WorldItem", "WorldShip", "WorldPilotImplant", "WorldBooster", "WorldStructure"];
        long last = names.Select(name => _sequences.GetValueOrDefault(name, store.Sequence(name))).Max();
        return Next("WorldItem", Math.Max(last, InventoryDefinitions.FirstOwnedItemId));
    }

    internal bool Overlay<T>(T row)
        where T : class
    {
        if (row is OwnedItem item)
        {
            return owner.CombatForKnownPilot(item.OwnerId)?.OverlayEconomicItem(item) ?? true;
        }
        return true;
    }

    internal bool Visible<T>(T row)
        where T : class =>
        row switch
        {
            OwnedItem item => Set<SavedContractEscrow>().Find(item.Id) is null,
            Ship ship => Set<SavedContractEscrow>().Find(ship.Id) is null,
            Pilot pilot => pilot.ActiveShip is not null && pilot.ActiveShip.OwnerId == pilot.Id,
            _ => true,
        };

    internal void Link<T>(T row)
        where T : class
    {
        if (row is Pilot pilot)
        {
            pilot.ActiveShip = Ships.Find(pilot.ActiveShipId)!;
            pilot.Account = Set<Account>().Find(pilot.AccountId)!;
            if (pilot.ActiveShip is { } ship)
            {
                int system = ship.Position?.SystemId ?? database.Catalog.FindStation(ship.StationId)?.SolarSystemId ?? 0;
                if (system > 0)
                {
                    owner.TrackShip(ship.Id, system, pilot.Id);
                }
            }
        }
        else if (row is Ship ship)
        {
            ship.Position = Positions.Find(ship.Id);
            if (ship.Position is { } position && owner.CombatForShip(ship.Id)?.InformationPosition(ship.Id) is { } current)
            {
                ship.Position = new FlightPosition
                {
                    ShipId = ship.Id,
                    SystemId = position.SystemId,
                    X = current.X,
                    Y = current.Y,
                    Z = current.Z,
                };
            }
        }
    }

    private IEnumerable<T> Retained<T>()
        where T : class => Set<T>().Local.Where(row => Set<T>().State(row) != EntityState.Deleted).ToArray();

    private void Validate()
    {
        foreach (var item in Retained<OwnedItem>())
        {
            if (
                item.Id <= 0
                || item.Quantity <= 0
                || !double.IsFinite(item.CrystalDamage)
                || item.VolumeOverride is double volume && !double.IsFinite(volume)
                || Set<InventoryOwner>().Find(item.OwnerId) is null
            )
            {
                throw new InvalidDataException("The resident item has an invalid quantity or owner.");
            }
        }
        foreach (var ship in Retained<Ship>())
        {
            if (
                ship.Id <= 0
                || Set<InventoryOwner>().Find(ship.OwnerId) is null
                || store.ActivePilot(ship.Id) is long active && active != ship.OwnerId
            )
            {
                throw new InvalidDataException("The resident ship has an invalid owner or active pilot.");
            }
        }
        foreach (var position in Retained<FlightPosition>())
        {
            if (
                Ships.Find(position.ShipId) is null
                || !double.IsFinite(position.X)
                || !double.IsFinite(position.Y)
                || !double.IsFinite(position.Z)
            )
            {
                throw new InvalidDataException("The resident position has no ship.");
            }
        }
        foreach (var wallet in Retained<PilotWallet>())
        {
            if (wallet.Cents < 0 || !store.IsPilot(wallet.PilotId))
            {
                throw new InvalidDataException("The resident wallet has an invalid balance or pilot.");
            }
        }
        foreach (var order in Retained<SavedPersonalOrder>())
        {
            if (
                order.Remaining < 0
                || order.Entered <= 0
                || order.Remaining > order.Entered
                || order.PriceCents <= 0
                || order.EscrowCents < 0
                || !store.IsPilot(order.PilotId)
            )
            {
                throw new InvalidDataException("The resident market order is invalid.");
            }
        }
        foreach (var order in Retained<NpcMarketOrder>())
        {
            if (
                order.Remaining < 0
                || order.Entered <= 0
                || order.Remaining > order.Entered
                || order.PriceCents <= 0
                || Set<NpcMarketOrder>()
                    .Where(o => o.StationId == order.StationId && o.TypeId == order.TypeId)
                    .Any(o => o.Id != order.Id && o.Bid == order.Bid && o.Kind == order.Kind)
            )
            {
                throw new InvalidDataException("The resident NPC stock is invalid or duplicates an existing assortment.");
            }
        }
        foreach (var stock in Retained<SavedRegionalMarketStock>())
        {
            if (Set<NpcMarketOrder>().Find(stock.OrderId) is null)
            {
                throw new InvalidDataException("The resident stock has no market order.");
            }
        }
        foreach (var trade in Retained<MarketExecution>())
        {
            if (
                !store.IsPilot(trade.PilotId)
                || trade.PriceCents <= 0
                || trade.Quantity <= 0
                || trade.TaxCents < 0
                || trade.BalanceCents < 0
            )
            {
                throw new InvalidDataException("The resident execution has invalid amounts or pilot.");
            }
        }
        foreach (var owner in Set<InventoryOwner>().Local.Where(o => Set<InventoryOwner>().State(o) == EntityState.Deleted).ToArray())
        {
            if (Set<OwnedItem>().Any(i => i.OwnerId == owner.Id) || Ships.Any(ship => ship.OwnerId == owner.Id))
            {
                throw new InvalidDataException("An inventory owner with assets cannot be removed.");
            }
        }
    }

    internal void Commit()
    {
        if (_completed)
        {
            throw new InvalidOperationException("This economic transaction already committed.");
        }
        Validate();
        var sets = _sets.Values.ToArray();
        foreach (var set in sets)
        {
            set.Prepare();
        }
        var changes = sets.SelectMany(set => set.Changes).ToArray();
        long[] pilots = sets.SelectMany(set => set.Owners).Where(store.IsPilot).Distinct().ToArray();
        foreach (var set in sets)
        {
            set.Publish();
        }
        store.Remember(_preparations);
        store.Committed(_sequences, changes, new(pilots: pilots));
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        store.End();
        systems.Dispose();
        lease.Dispose();
    }
}

internal sealed class EconomyTracking(EconomyTransaction transaction)
{
    internal IEnumerable<EconomyEntry<T>> Entries<T>()
        where T : class => transaction.Set<T>().Local.Select(row => transaction.Entry(row));
}
