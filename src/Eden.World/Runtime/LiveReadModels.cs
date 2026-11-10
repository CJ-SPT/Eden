using System.Collections.Frozen;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mail;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Characters;
using Eden.World.Loot;
using Eden.World.Mail;
using Eden.World.Mining;
using Eden.World.Runtime.Combat;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels(LiveWorld owner, WorldDatabase database)
{
    private readonly ResidentSnapshot<int, long> _resourceDeadline = new(
        "resident.resourceDeadline",
        "WorldBeltRenewal",
        "WorldEconomicPolicy"
    );

    internal bool TryResourceDeadline(out long deadline) => _resourceDeadline.TryRead(0, 0, out deadline);

    internal void ScheduleResources(long deadline) => _resourceDeadline.Publish(0, deadline, 0, long.MaxValue);

    private readonly ResidentSnapshot<(long Pilot, long Ship), IReadOnlyList<FittedModuleCondition>> _moduleConditions = new(
        "resident.moduleConditions",
        "WorldModuleCondition",
        "WorldFitRevision",
        "WorldPilot",
        "WorldShip"
    );

    internal IReadOnlyList<FittedModuleCondition> ModuleConditions(
        long pilot,
        long ship,
        Func<IReadOnlyList<FittedModuleCondition>> load
    ) => Read(_moduleConditions, (pilot, ship), load);

    private readonly ResidentSnapshot<long, LiveLearningState> _learning = new(
        "resident.learning",
        "WorldPilot",
        "WorldCharacterSheet",
        "WorldJumpClone",
        "WorldPilotImplant",
        "WorldImplantState",
        "WorldBooster",
        "WorldBoosterState",
        "WorldSkillInjectorState"
    );
    private readonly ResidentSnapshot<long, FreeSkillPointBalance> _points = new(
        "resident.freePoints",
        "WorldPilot",
        "WorldFreeSkillPoints"
    );
    private readonly ResidentSnapshot<long, long> _extraction = new("resident.extraction", "WorldSkillExtraction");
    private readonly ResidentSnapshot<long, IReadOnlyList<LoyaltyBalance>> _loyalty = new(
        "resident.loyalty",
        "WorldPilot",
        "WorldLoyaltyWallet"
    );
    private readonly ResidentSnapshot<long, long> _wallet = new("resident.wallet", "WorldWallet");
    private readonly ResidentSnapshot<long, long> _mailHead = new("resident.mailHead", "WorldMailChange");
    private readonly ResidentSnapshot<long, long> _listHead = new("resident.listHead", "WorldMailingListChange");
    private readonly ResidentSnapshot<long, FrozenDictionary<DogmaAttributeId, double>> _fits = new(
        "resident.fit",
        "WorldFitRevision",
        "WorldAccount"
    );
    private readonly ResidentSnapshot<int, bool> _unfinishedMissiles = new("resident.unfinishedMissiles", "WorldMissile");

    internal bool UnfinishedMissiles(int system, Func<bool> load) => Read(_unfinishedMissiles, system, load);

    private readonly ResidentSnapshot<int, IReadOnlyList<StaticSpaceObject>> _asteroids = new("resident.asteroids", "WorldAsteroid");
    private readonly ResidentSnapshot<(int System, MiningFamily Family, long Catalog), MiningSiteView> _miningSites = new(
        "resident.miningSites",
        "WorldMiningSite"
    );

    private sealed record WreckView(IReadOnlyList<WreckContact> Contacts, long NextExpiry, FrozenSet<long> Items);

    private readonly ResidentSnapshot<int, WreckView> _wrecks = new("resident.wrecks", "WorldWreck", "WorldItemMembership", "WorldShip");

    private readonly ResidentSnapshot<int, IReadOnlyList<DroneContact>> _drones = new(
        "resident.drones",
        "WorldDrone",
        "WorldItem",
        "WorldFitRevision",
        "WorldPilot",
        "WorldShip"
    );
    private readonly ResidentSnapshot<int, IReadOnlySet<(long Pilot, long Ship)>> _droneControllers = new(
        "resident.droneControllers",
        "WorldDrone"
    );

    internal IReadOnlyList<DroneContact> Drones(int system, Func<IReadOnlyList<DroneContact>> load) =>
        Read(_drones, system, () => Array.AsReadOnly(load().ToArray()));

    internal IReadOnlySet<(long Pilot, long Ship)> DroneControllers(Func<IReadOnlySet<(long Pilot, long Ship)>> load) =>
        Read(_droneControllers, 0, load);

    internal static LiveReadModels? Find(string path) => LiveWorld.Find(path)?.Reads;

    private T Read<TKey, T>(ResidentSnapshot<TKey, T> cache, TKey key, Func<T> load, long now = 0, Func<T, long>? expires = null)
        where TKey : notnull
    {
        return owner.Run(() => ReadOwned<TKey, T>(cache, key, load, now, expires), nameof(Read));
    }

    private T ReadOwned<TKey, T>(ResidentSnapshot<TKey, T> cache, TKey key, Func<T> load, long now = 0, Func<T, long>? expires = null)
        where TKey : notnull
    {
        using var phase = SessionPerformance.Phase(cache.Name);
        using var ownership = owner.CurrentSystem is null ? owner.EnterShared() : owner.Enter();
        // Never publish data read from a caller's transaction, including rolled-back
        // or partially tracked changes. Ordinary stores remain the mutation owners.
        if (owner.SqlConnections != 0)
        {
            return load();
        }
        if (cache.TryRead(key, now, out var cached))
        {
            return cached;
        }
        var value = load();
        cache.Publish(
            key,
            value,
            now,
            expires?.Invoke(value) ?? long.MaxValue,
            checked(ResidentSnapshot<TKey, T>.MinimumEntries + owner.CapsuleerJobs.Count)
        );
        return value;
    }

    internal LiveLearningState Learning(long pilotId) =>
        Read(
            _learning,
            pilotId,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                world.Database.OpenConnection();
                var implants = PilotImplantStore.Read(world, pilotId);
                var boosters = world.Set<SavedBooster>().Where(b => b.PilotId == pilotId).OrderBy(b => b.Slot).AsNoTracking().ToArray();
                var injector = world.Set<SavedSkillInjectorState>().Find(pilotId);
                return new(
                    implants with
                    {
                        Implants = Array.AsReadOnly(implants.Implants.ToArray()),
                    },
                    Array.AsReadOnly(boosters.Select(b => new LiveLearningBooster(b.Snapshot(), b.RemainingInjectionUses)).ToArray()),
                    world.Set<SavedBoosterState>().Find(pilotId)?.Revision ?? 0,
                    injector?.NextAlphaInjection ?? 0,
                    injector?.Revision ?? 0
                );
            }
        );

    internal FreeSkillPointBalance Points(long pilot, Func<FreeSkillPointBalance> load) => Read(_points, pilot, load);

    internal long Extraction(long pilot, Func<long> load) => Read(_extraction, pilot, load);

    internal long Wallet(long pilot, Func<long> load) => Read(_wallet, pilot, load);

    internal bool HasMailChanges(long pilot, long cursor) =>
        cursor
        < Read(
            _mailHead,
            pilot,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                return world.Set<SavedMailChange>().Where(c => c.PilotId == pilot).Max(c => (long?)c.Id) ?? 0;
            }
        );

    internal bool HasListChanges(long pilot, long cursor) =>
        cursor
        < Read(
            _listHead,
            pilot,
            () =>
            {
                using WorldDbContext world = new(database.Path);
                return world.Set<SavedMailingListChange>().Where(c => c.PilotId == pilot).Max(c => (long?)c.Id) ?? 0;
            }
        );

    internal IReadOnlyList<LoyaltyBalance> Loyalty(long pilot, Func<IReadOnlyList<LoyaltyBalance>> load) =>
        Read(_loyalty, pilot, () => Array.AsReadOnly(load().ToArray()));

    internal IReadOnlyDictionary<DogmaAttributeId, double> Fit(long pilot, Func<IReadOnlyDictionary<DogmaAttributeId, double>> load) =>
        Read(_fits, pilot, () => load().ToFrozenDictionary());

    internal IReadOnlyList<StaticSpaceObject> Asteroids(int system, Func<IReadOnlyList<StaticSpaceObject>> load) =>
        Read(_asteroids, system, () => Array.AsReadOnly(load().ToArray()));

    internal MiningSiteView MiningSites(int system, MiningFamily family, long catalog, long now, Func<MiningSiteView> load) =>
        Read(_miningSites, (system, family, catalog), load, now, view => view.Deadline);

    internal IReadOnlyList<WreckContact> Wrecks(int system, long now, Func<IReadOnlyList<WreckContact>> load) =>
        Read(
            _wrecks,
            system,
            () =>
            {
                var contacts = Array.AsReadOnly(load().ToArray());
                using WorldDbContext world = new(database.Path);
                long next =
                    world.Set<WorldWreck>().Where(w => w.SystemId == system && w.ExpiresAt > now).Min(w => (long?)w.ExpiresAt)
                    ?? long.MaxValue;
                var ids = contacts.Select(c => c.Id).ToArray();
                var items = world.Set<OwnedItem>().Where(i => ids.Contains(i.LocationId)).Select(i => i.Id).ToArray().ToFrozenSet();
                return new WreckView(contacts, next, items);
            },
            now,
            view => view.NextExpiry
        ).Contacts;

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes)
    {
        _information.Observe(
            changes,
            (key, change) =>
                change.Table switch
                {
                    "WorldPilot" => key.Pilot == change.Id,
                    "WorldShip" => key.Ship == change.Id,
                    _ => key.Module == change.Id,
                }
        );
        foreach (var change in changes)
        {
            if (change.Table == "WorldPilot")
            {
                _gateWatcherShips.Remove(change.Id);
            }
            if (change.Table is "WorldPosition" or "WorldShip")
            {
                _gateWatcherPositions.Remove(change.Id);
            }
        }
        _directionalObservers.ObserveValues(
            changes,
            (pilot, view, change) =>
                change.Table switch
                {
                    "WorldPilot" or "WorldFitRevision" => pilot == change.Id,
                    "WorldShip" or "WorldPosition" => view.ShipId == change.Id,
                    _ => true,
                }
        );
        if (changes.Any(c => c.Table == "schema"))
        {
            Clear();
            return;
        }
        ObserveSystemContacts(changes);
        ObserveSpatialNavigation(changes);
        _spatialMembership.Observe(
            changes,
            (key, change) =>
                change.Table switch
                {
                    "WorldPilot" => key.Pilot == change.Id,
                    "WorldShip" => key.Ship == change.Id,
                    _ => key.Module == change.Id,
                }
        );
        _resourceDeadline.Observe(changes);
        _moduleConditions.ObserveValues(
            changes,
            (key, conditions, change) =>
                change.Table switch
                {
                    "WorldPilot" or "WorldFitRevision" => key.Pilot == change.Id,
                    "WorldShip" => key.Ship == change.Id,
                    _ => conditions.Any(c => c.Condition.ItemId == change.Id),
                }
        );
        _missionSites.Observe(changes);
        _learning.Observe(changes, (pilot, change) => change.Table != "WorldPilot" || pilot == change.Id);
        _points.Observe(changes, (pilot, change) => pilot == change.Id);
        _extraction.Observe(changes, (pilot, change) => pilot == change.Id);
        _loyalty.Observe(changes);
        _wallet.Observe(changes, (pilot, change) => pilot == change.Id);
        _mailHead.Observe(changes);
        _listHead.Observe(changes);
        _fits.Observe(changes, (pilot, change) => change.Table == "WorldAccount" || pilot == change.Id);
        ObserveDefenseProfiles(changes);
        _asteroids.Observe(changes);
        _unfinishedMissiles.Observe(changes);
        _miningSites.Observe(changes);
        ObserveStructures(changes);
        _droneControllers.Observe(changes);
        _corporationChangeHeads.Observe(changes);
        _corporationLoyalty.Observe(changes, (pilot, change) => change.Table != "WorldPilot" || pilot == change.Id);
        _corporations.Observe(changes);
        _corporationIdentities.Observe(changes, (pilot, change) => change.Table != "WorldPilot" || pilot == change.Id);
        _crimewatch.Observe(changes);
        _concord.Observe(changes);
        _concordOffline.Observe(changes);
        _loggedOff.Observe(changes);
        ObserveSpace(changes);
        ObservePolling(changes);
    }

    internal void Clear()
    {
        _information.Clear();
        _gateWatcherShips.Clear();
        _gateWatcherPositions.Clear();
        _directionalObservers.Clear();
        _directionalContacts.Clear();
        _spatialNavigation.Clear();
        _spatialMembership.Clear();
        _resourceDeadline.Clear();
        _moduleConditions.Clear();
        _missionSites.Clear();
        _learning.Clear();
        _points.Clear();
        _extraction.Clear();
        _loyalty.Clear();
        _wallet.Clear();
        _mailHead.Clear();
        _listHead.Clear();
        _fits.Clear();
        _defenseProfiles.Clear();
        _asteroids.Clear();
        _unfinishedMissiles.Clear();
        _miningSites.Clear();
        _structures.Clear();
        _structureNames.Clear();
        _wrecks.Clear();
        _drones.Clear();
        _droneControllers.Clear();
        _corporationChangeHeads.Clear();
        _corporationLoyalty.Clear();
        _corporations.Clear();
        _corporationIdentities.Clear();
        _crimewatch.Clear();
        _concord.Clear();
        _concordOffline.Clear();
        _loggedOff.Clear();
        ClearSpace();
        ClearPolling();
    }
}
