using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Sovereignty;

[Injectable(InjectionType.Singleton)]
internal sealed partial class SovereigntyResourceStore(WorldDatabase database, TimeProvider clock, IPilotEntosis? entosis = null)
    : ISovereigntyResources
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Sovereignty resources require the resident world.");
    private LiveWorld Live
    {
        get
        {
            entosis?.AdvanceAll();
            return Resident;
        }
    }
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public IReadOnlyList<SovereigntyUpgradeDefinition> Definitions()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return SovereigntyResourceCatalog.Upgrades(catalog);
    }

    public bool OwnsPilot(long accountId, long pilotId) =>
        Resident.Economy.Inspect(world => world.Pilots.Find(pilotId)?.AccountId == accountId);

    public int Corporation(long pilotId) => Resident.Economy.Inspect(world => world.Pilots.Find(pilotId)!.CorporationId);

    public int CurrentSystem(long pilotId) =>
        Resident.Economy.Inspect(world =>
        {
            var ship = world.Pilots.Find(pilotId)!.ActiveShip;
            if (ship.Position?.SystemId is int system)
            {
                return system;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            return catalog.RequireStation(ship.StationId).SolarSystemId;
        });

    public bool CanManageHub(long pilotId, long hubId) =>
        Resident.Economy.Inspect(world =>
        {
            try
            {
                Require(world, pilotId, hubId, true);
                return true;
            }
            catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException)
            {
                return false;
            }
        });

    public void AdvanceAll()
    {
        entosis?.AdvanceAll();
        if (
            LiveWorld.Find(database.Path) is not { } live
            || !live.Economy.Inspect(world => world.Set<SavedSovereigntyClaim>().Any() || world.Set<SavedSovereigntySkyhook>().Any())
        )
        {
            return;
        }
        using var action = live.Economy.Begin(TransactionParticipants.None);
        using CatalogDbContext catalog = new(database.CatalogPath);
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        action.Commit();
    }

    private static SavedSovereigntyClaim Require(WorldAction world, long pilotId, long hubId, bool manage = false)
    {
        var claim =
            world.Filter<SavedSovereigntyClaim>(row => row.HubId == hubId).SingleOrDefault()
            ?? throw new InvalidDataException("This sovereignty hub has no active claim.");
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Choose your selected pilot.");
        if (
            world.Set<SavedAllianceMember>().Find(pilot.CorporationId)?.AllianceId != claim.AllianceId
            || world.Set<SavedAlliance>().Find(claim.AllianceId) is not { ClosedAt: null }
        )
        {
            throw new UnauthorizedAccessException("This hub belongs to another alliance.");
        }
        if (manage)
        {
            var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
            if (
                pilot.CorporationId != claim.CorporationId
                || corporation is null
                || !CorporationRoleStore.Effective(world, pilotId, corporation).Roles.Has(CorporationRole.StationManager)
            )
            {
                throw new UnauthorizedAccessException("The owning corporation's structure manager is required.");
            }
        }
        return claim;
    }

    private static void InRange(WorldAction world, CatalogDbContext catalog, long pilotId, SavedStructure structure)
    {
        if (world.Set<Eden.Data.World.Combat.ShipLoss>().Find(world.Pilots.Find(pilotId)!.ActiveShipId) is not null)
        {
            throw new InvalidDataException("Use a surviving active ship.");
        }
        var position = world.Pilots.Find(pilotId)!.ActiveShip.Position;
        if (
            position?.SystemId != structure.SystemId
            || new Eden.Gameplay.Flight.SpacePosition(position.X, position.Y, position.Z).DistanceTo(
                new(structure.X, structure.Y, structure.Z)
            )
                - (catalog.RequireItem(structure.TypeId).Radius ?? 0)
                - (catalog.RequireItem(world.Pilots.Find(pilotId)!.ActiveShip.TypeId).Radius ?? 0)
                > SovereigntyResourceRules.InteractionRange
        )
        {
            throw new InvalidDataException("Move within sovereignty resource interaction range in your active ship.");
        }
        world.RequireUncloaked(world.Pilots.Find(pilotId)!.ActiveShipId);
    }

    private static OwnedItem Source(WorldAction world, long pilotId, long itemId)
    {
        var pilot = world.Pilots.Find(pilotId)!;
        var item = world.Set<OwnedItem>().Find(itemId);
        if (
            item is null
            || item.Singleton
            || item.Quantity <= 0
            || item.LocationId != pilot.ActiveShipId
            || item.OwnerId != pilotId && item.OwnerId != pilot.CorporationId
            || item.FlagId is not ((int)InventoryFlag.Cargo or (int)InventoryFlag.FleetHangar or (int)InventoryFlag.InfrastructureHold)
        )
        {
            throw new InvalidDataException("Use a packaged item in your active ship's cargo, fleet or infrastructure hold.");
        }
        return item;
    }

    private static InventoryChange Debit(WorldAction world, CatalogDbContext catalog, OwnedItem item, int quantity)
    {
        if (quantity <= 0 || quantity > item.Quantity)
        {
            throw new InvalidDataException("Choose an available positive quantity.");
        }
        var before = PilotInventoryStore.Read(catalog, item);
        item.Quantity -= quantity;
        if (item.Quantity == 0)
        {
            world.Remove(item);
        }
        return new(before with { Quantity = item.Quantity }, before);
    }

    public SovereigntyResourceView Read(long pilotId, long hubId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var claim = Require(action, pilotId, hubId);
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        var result = SovereigntyResourcePersistence.View(action, catalog, claim, Now);
        action.Commit();
        return result;
    }

    public SovereigntyResourceResult Configure(
        long pilotId,
        long hubId,
        IReadOnlyList<long> items,
        IReadOnlyList<SovereigntyUpgradeSetting> configuration
    )
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var claim = Require(action, pilotId, hubId, true);
        long now = Now;
        SovereigntyResourcePersistence.Advance(action, catalog, now);
        if (items.Distinct().Count() != items.Count || configuration.Select(row => row.TypeId).Distinct().Count() != configuration.Count)
        {
            throw new InvalidDataException("Do not repeat installation items or configured upgrade types.");
        }
        var definitions = SovereigntyResourceCatalog.Upgrades(catalog).ToDictionary(row => row.TypeId);
        var installed = action.Filter<SavedSovereigntyUpgrade>(row => row.HubId == hubId).ToArray();
        var sources = items.Select(id => Source(action, pilotId, id)).ToArray();
        if (sources.Length > 0)
        {
            InRange(action, catalog, pilotId, action.Set<SavedStructure>().Find(hubId)!);
        }
        int[] types = installed.Select(row => row.TypeId).Concat(sources.Select(row => row.TypeId)).ToArray();
        if (
            types.Distinct().Count() != types.Length
            || types.Any(type => !definitions.ContainsKey(type))
            || !types.Order().SequenceEqual(configuration.Select(row => row.TypeId).Order())
        )
        {
            throw new InvalidDataException("Configure every installed upgrade exactly once, using published sovereignty upgrades.");
        }
        var potential = SovereigntyResourceCatalog.System(catalog, claim.SystemId);
        if (
            types.Sum(type => definitions[type].Power)
            > potential.StarPower + potential.Planets.Sum(row => row.Power) + types.Sum(type => definitions[type].PowerProduced)
        )
        {
            throw new InvalidDataException("The installed upgrades exceed this system's potential power.");
        }
        var changes = sources.Select(item => Debit(action, catalog, item, 1)).ToArray();
        foreach (var source in sources)
        {
            action.Add(
                new SavedSovereigntyUpgrade
                {
                    Id = action.Next("WorldSovereigntyUpgrade"),
                    HubId = hubId,
                    SystemId = claim.SystemId,
                    TypeId = source.TypeId,
                    PowerState = SovereigntyUpgradePower.Offline,
                }
            );
        }
        for (int priority = 0; priority < configuration.Count; priority++)
        {
            var setting = configuration[priority];
            var row = action.Filter<SavedSovereigntyUpgrade>(row => row.HubId == hubId && row.TypeId == setting.TypeId).Single();
            row.Priority = priority;
            row.ConfiguredOnline = setting.Online;
            if (!setting.Online)
            {
                row.StartupPaid = false;
            }
        }
        SovereigntyResourcePersistence.Refresh(action, catalog, now);
        var result = SovereigntyResourcePersistence.View(action, catalog, claim, now);
        action.Commit();
        return new(result, changes);
    }

    public void DestroyUpgrade(long pilotId, long hubId, int typeId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        Require(action, pilotId, hubId, true);
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        var row =
            action.Filter<SavedSovereigntyUpgrade>(row => row.HubId == hubId && row.TypeId == typeId).SingleOrDefault()
            ?? throw new InvalidDataException("This upgrade is not installed.");
        action.Remove(row);
        SovereigntySites.Invalidate(action, row.SystemId);
        SovereigntyResourcePersistence.Refresh(action, catalog, Now);
        action.Commit();
    }

    public SovereigntyResourceResult AddFuel(long pilotId, long hubId, long itemId, int quantity)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var claim = Require(action, pilotId, hubId, true);
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        InRange(action, catalog, pilotId, action.Set<SavedStructure>().Find(hubId)!);
        var item = Source(action, pilotId, itemId);
        if (!SovereigntyResourceCatalog.Upgrades(catalog).Any(row => row.FuelTypeId == item.TypeId))
        {
            throw new InvalidDataException("Only sovereignty upgrade reagents can be deposited.");
        }
        var row =
            action.Filter<SavedSovereigntyFuel>(row => row.HubId == hubId && row.TypeId == item.TypeId).SingleOrDefault()
            ?? action
                .Add(
                    new SavedSovereigntyFuel
                    {
                        Id = action.Next("WorldSovereigntyFuel"),
                        HubId = hubId,
                        TypeId = item.TypeId,
                    }
                )
                .Entity;
        var change = Debit(action, catalog, item, quantity);
        row.Amount = checked(row.Amount + quantity);
        if (row.Amount > uint.MaxValue)
        {
            throw new InvalidDataException("The sovereignty fuel ledger is full.");
        }
        SovereigntyResourcePersistence.Refresh(action, catalog, Now);
        var result = SovereigntyResourcePersistence.View(action, catalog, claim, Now);
        action.Commit();
        return new(result, [change]);
    }

    public void ConfigureWorkforce(long pilotId, long hubId, SovereigntyWorkforceConfiguration configuration)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var claim = Require(action, pilotId, hubId, true);
        if (
            !Enum.IsDefined(configuration.Mode)
            || configuration.Amount < 0
            || configuration.Sources is { Count: > SovereigntyResourceRules.MaximumWorkforceImports }
            || configuration.Sources?.Distinct().Count() != configuration.Sources?.Count
            || configuration.Mode != SovereigntyWorkforceMode.Import && configuration.Sources is { Count: > 0 }
            || configuration.Mode != SovereigntyWorkforceMode.Export && (configuration.Destination is not null || configuration.Amount != 0)
        )
        {
            throw new InvalidDataException("Choose a valid workforce mode, destination and up to three distinct sources.");
        }
        foreach (int system in (configuration.Sources ?? []).Concat(configuration.Destination is int target ? [target] : []))
        {
            if (system == claim.SystemId || action.Set<SavedSovereigntyClaim>().Find(system)?.AllianceId != claim.AllianceId)
            {
                throw new InvalidDataException("Workforce may only connect other systems claimed by this alliance.");
            }
        }
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        var row =
            action.Set<SavedSovereigntyWorkforce>().Find(hubId)
            ?? action.Add(new SavedSovereigntyWorkforce { HubId = hubId, SystemId = claim.SystemId }).Entity;
        row.Mode = configuration.Mode;
        row.Destination = configuration.Destination;
        row.Amount = configuration.Amount;
        action.RemoveRange(action.Filter<SavedSovereigntyWorkforceSource>(source => source.HubId == hubId).ToArray());
        foreach (int source in configuration.Sources ?? [])
        {
            action.Add(
                new SavedSovereigntyWorkforceSource
                {
                    Id = action.Next("WorldSovereigntyWorkforceSource"),
                    HubId = hubId,
                    SourceSystemId = source,
                }
            );
        }
        SovereigntyResourcePersistence.Refresh(action, catalog, Now);
        action.Commit();
    }

    public IReadOnlyList<SovereigntyNetworkHub> Network(long pilotId, long hubId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var claim = Require(action, pilotId, hubId);
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        var result = SovereigntyResourcePersistence
            .Network(action, catalog, Now)
            .Where(row => row.AllianceId == claim.AllianceId)
            .ToArray();
        action.Commit();
        return result;
    }
}
