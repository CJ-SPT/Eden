using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Sovereignty;

internal sealed partial class SovereigntyResourceStore
{
    // Eden placement policy; the exported component names an orbital function but contains no altitude.
    private const double SkyhookOrbitAltitude = 5_000_000;
    private const double SkyhookPlanetVicinity = 50_000_000;

    public bool IsSkyhookItem(long pilotId, long itemId) =>
        Resident.Economy.Inspect(world =>
            world.Set<OwnedItem>().Find(itemId) is { TypeId: SovereigntyResourceRules.SkyhookTypeId } item
            && (item.OwnerId == pilotId || item.OwnerId == world.Pilots.Find(pilotId)!.CorporationId)
        );

    public bool CanManageSkyhook(long pilotId, long id) =>
        Resident.Economy.Inspect(world =>
        {
            try
            {
                RequireSkyhook(world, pilotId, id, true);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        });

    private static void SkyhookManager(WorldAction world, long pilotId, int corporationId)
    {
        var pilot = world.Pilots.Find(pilotId)!;
        var corporation = world.Set<SavedCorporation>().Find(corporationId);
        if (
            pilot.CorporationId != corporationId
            || corporation is null
            || !CorporationRoleStore.Effective(world, pilotId, corporation).Roles.Has(CorporationRole.StationManager)
        )
        {
            throw new UnauthorizedAccessException("The owning corporation's structure manager is required.");
        }
    }

    public SovereigntySkyhookResult DeploySkyhook(long pilotId, long itemId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = action.Pilots.Find(pilotId)!;
        SkyhookManager(action, pilotId, pilot.CorporationId);
        var item = Source(action, pilotId, itemId);
        if (item.TypeId != SovereigntyResourceRules.SkyhookTypeId)
        {
            throw new InvalidDataException("Choose an Orbital Skyhook.");
        }
        var position = pilot.ActiveShip.Position ?? throw new InvalidDataException("Deploy a skyhook from your ship in space.");
        int system = position.SystemId ?? throw new InvalidDataException("Choose an eligible solar system.");
        var eligibility = new SovereigntyStore(database).System(system);
        if (eligibility is null || !SovereigntyRules.CanClaim(eligibility))
        {
            throw new InvalidDataException("Skyhooks require claimable null security space.");
        }
        if (action.Set<Eden.Data.World.Combat.ShipLoss>().Find(pilot.ActiveShipId) is not null)
        {
            throw new InvalidDataException("Deploy from a surviving active ship.");
        }
        action.RequireUncloaked(pilot.ActiveShipId);
        var point = new SpacePosition(position.X, position.Y, position.Z);
        var planets = SovereigntyResourceCatalog.System(catalog, system).Planets;
        var planet =
            catalog
                .CelestialsInSystem(system)
                .Where(row => planets.Any(p => p.PlanetId == row.Id))
                .OrderBy(row => point.DistanceTo(new(row.X, row.Y, row.Z)))
                .FirstOrDefault()
            ?? throw new InvalidDataException("This system has no resource-bearing planet.");
        if (point.DistanceTo(new(planet.X, planet.Y, planet.Z)) > planet.Radius + SkyhookPlanetVicinity)
        {
            throw new InvalidDataException("Deploy a skyhook near a planet.");
        }
        if (
            action
                .Filter<SavedSovereigntySkyhook>(row => row.PlanetId == planet.Id)
                .AsEnumerable()
                .Any(row => action.Set<SavedStructure>().Find(row.Id) is { State: not StructureState.Destroyed })
        )
        {
            throw new InvalidDataException("This planet already has an Orbital Skyhook.");
        }
        long now = Now;
        SovereigntyResourcePersistence.Advance(action, catalog, now);
        var component = catalog.Require(CatalogData.Client.Spacecomponentsbytype, (long)item.TypeId)["activate"]["durationSeconds"];
        long seconds = component is Eden.Data.Catalog.ClientSpacecomponentsbytypeValueValueChoiceLong duration
            ? duration.Value
            : throw new InvalidDataException("Missing skyhook activation duration.");
        long id = Math.Max(InventoryPersistence.NextId(action), StructureRules.FirstStructureId + 1);
        var orbit = EntosisRules.Placement(new(planet.X, planet.Y, planet.Z), point, (planet.Radius ?? 0) + SkyhookOrbitAltitude);
        action.Add(
            new SavedStructure
            {
                Id = id,
                TypeId = item.TypeId,
                CorporationId = pilot.CorporationId,
                SystemId = system,
                Name = catalog.RequireItem(item.TypeId).Name,
                State = StructureState.ShieldVulnerable,
                StateStartedAt = now,
                DeployedAt = now,
                X = orbit.X,
                Y = orbit.Y,
                Z = orbit.Z,
            }
        );
        var hook = action
            .Add(
                new SavedSovereigntySkyhook
                {
                    Id = id,
                    SystemId = system,
                    PlanetId = planet.Id,
                    CorporationId = pilot.CorporationId,
                    Active = true,
                    OnlineAt = checked(now + seconds * TimeSpan.TicksPerSecond),
                    UpdatedAt = checked(now + seconds * TimeSpan.TicksPerSecond),
                }
            )
            .Entity;
        var change = Debit(action, catalog, item, 1);
        SovereigntyResourcePersistence.Refresh(action, catalog, now);
        var result = SkyhookView(action, catalog, hook, Now);
        action.Commit();
        return new(result, [change]);
    }

    internal static SovereigntySkyhook SkyhookView(WorldAction world, CatalogDbContext catalog, SavedSovereigntySkyhook row, long now) =>
        new(
            row.Id,
            row.SystemId,
            row.PlanetId,
            row.CorporationId,
            world.Set<SavedAllianceMember>().Find(row.CorporationId)?.AllianceId ?? 0,
            row.Active && now >= row.OnlineAt,
            row.OnlineAt,
            SovereigntyResourceCatalog.System(catalog, row.SystemId).Planets.Single(p => p.PlanetId == row.PlanetId).Reagent?.TypeId ?? 0,
            row.Secured,
            row.Surplus,
            row.UpdatedAt
        )
        {
            Resources = SovereigntyResourceCatalog.System(catalog, row.SystemId).Planets.Single(p => p.PlanetId == row.PlanetId),
        };

    private static SavedSovereigntySkyhook RequireSkyhook(WorldAction world, long pilotId, long id, bool manage = false)
    {
        var hook = world.Set<SavedSovereigntySkyhook>().Find(id) ?? throw new InvalidDataException("This Orbital Skyhook does not exist.");
        if (world.Set<SavedStructure>().Find(id) is not { State: not StructureState.Destroyed })
        {
            throw new InvalidDataException("This Orbital Skyhook was destroyed.");
        }
        if (manage)
        {
            SkyhookManager(world, pilotId, hook.CorporationId);
        }
        return hook;
    }

    public IReadOnlyList<SovereigntySkyhook> Skyhooks(long pilotId, bool corporationOnly = false)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = action.Pilots.Find(pilotId)!;
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        var result = action
            .Set<SavedSovereigntySkyhook>()
            .AsEnumerable()
            .Where(row => corporationOnly ? row.CorporationId == pilot.CorporationId : row.SystemId == pilot.ActiveShip.Position?.SystemId)
            .Where(row => action.Set<SavedStructure>().Find(row.Id) is { State: not StructureState.Destroyed })
            .Select(row => SkyhookView(action, catalog, row, Now))
            .ToArray();
        action.Commit();
        return result;
    }

    public SovereigntySkyhook Skyhook(long pilotId, long id)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var hook = RequireSkyhook(action, pilotId, id);
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        var result = SkyhookView(action, catalog, hook, Now);
        action.Commit();
        return result;
    }

    public void SetSkyhookActive(long pilotId, long id, bool active)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var hook = RequireSkyhook(action, pilotId, id, true);
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        if (hook.Active != active)
        {
            hook.Active = active;
            hook.UpdatedAt = Math.Max(hook.UpdatedAt, Now);
        }
        SovereigntyResourcePersistence.Refresh(action, catalog, Now);
        action.Commit();
    }

    public SovereigntySkyhookResult CollectReagents(long pilotId, long id, int quantity = int.MaxValue)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var hook = RequireSkyhook(action, pilotId, id, true);
        SovereigntyResourcePersistence.Advance(action, catalog, Now);
        InRange(action, catalog, pilotId, action.Set<SavedStructure>().Find(id)!);
        var pilot = action.Pilots.Find(pilotId)!;
        var snapshot = SkyhookView(action, catalog, hook, Now);
        if (hook.Secured == 0 || snapshot.ReagentTypeId == 0)
        {
            action.Commit();
            return new(snapshot, []);
        }
        if (quantity <= 0)
        {
            throw new InvalidDataException("Choose a positive reagent quantity.");
        }
        quantity = checked((int)Math.Min(quantity, hook.Secured));
        var attributes = Eden.World.Fittings.PilotFittingStore.ShipAttributes(action, catalog, pilotId);
        double free =
            ShipHoldRules.Capacity((int)InventoryFlag.Cargo, attributes)
            - InventoryPersistence.UsedVolume(action, catalog, pilotId, pilot.ActiveShipId, (int)InventoryFlag.Cargo);
        double volume = PilotInventoryStore.Volume(catalog, snapshot.ReagentTypeId);
        if (free < quantity * volume)
        {
            throw new InvalidDataException("Your cargo hold has insufficient capacity for the secured reagents.");
        }
        var change = InventoryPersistence.MergeOutput(
            action,
            catalog,
            pilotId,
            pilot.ActiveShipId,
            (int)InventoryFlag.Cargo,
            snapshot.ReagentTypeId,
            quantity
        );
        hook.Secured -= quantity;
        var result = SkyhookView(action, catalog, hook, Now);
        action.Commit();
        return new(result, [change]);
    }
}
