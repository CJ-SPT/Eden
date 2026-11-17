using Eden.Data.World.Alliances;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Structures;
using Eden.World.Thermal;

namespace Eden.World.Sovereignty;

[Injectable(InjectionType.Singleton)]
internal sealed partial class PilotEntosisStore(
    WorldDatabase database,
    ISovereignty sovereignty,
    IPilotTargetLocks locks,
    TimeProvider clock
) : IPilotEntosis
{
    // Paid reservations survive checkpoints; scoring leases deliberately do not.
    private readonly HashSet<long> _scoringLeases = [];
    private long _viewRevision;
    public long ViewRevision => Volatile.Read(ref _viewRevision);
    public bool Available => LiveWorld.Find(database.Path) is not null;
    private LiveWorld Live => LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Entosis requires the resident world.");
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public bool IsHubItem(long pilotId, long itemId) =>
        Live.Economy.Inspect(world =>
            world.Set<OwnedItem>().Find(itemId) is { TypeId: EntosisRules.HubTypeId } item && item.OwnerId == pilotId
        );

    public StructureResult Deploy(long pilotId, long itemId)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = action.Pilots.Find(pilotId) ?? throw new InvalidDataException("Choose your selected pilot.");
        var ship = pilot.ActiveShip;
        var position = ship.Position ?? throw new InvalidDataException("Deploy a sovereignty hub from your active ship in space.");
        int system = position.SystemId ?? throw new InvalidDataException("The ship has no current solar system.");
        var corporation = action.Set<SavedCorporation>().Find(pilot.CorporationId);
        var membership = action.Set<SavedAllianceMember>().Find(pilot.CorporationId);
        if (
            corporation is null
            || membership is null
            || action.Set<SavedAlliance>().Find(membership.AllianceId) is not { ClosedAt: null }
            || !CorporationRoleStore.Effective(action, pilotId, corporation).Roles.Has(CorporationRole.StationManager)
        )
        {
            throw new InvalidDataException("An alliance member corporation's structure manager must deploy the sovereignty hub.");
        }
        action.RequireUncloaked(ship.Id);
        var item = action.Set<OwnedItem>().Find(itemId);
        if (
            item is not { TypeId: EntosisRules.HubTypeId, Singleton: false, Quantity: > 0 }
            || item.OwnerId != pilotId
            || item.LocationId != ship.Id
            || item.FlagId is not ((int)InventoryFlag.Cargo or (int)InventoryFlag.FleetHangar or (int)InventoryFlag.InfrastructureHold)
        )
        {
            throw new InvalidDataException("Deploy a packaged sovereignty hub from your ship's cargo, fleet or infrastructure hold.");
        }
        if (sovereignty.System(system) is not { } definition || !SovereigntyRules.CanClaim(definition))
        {
            throw new InvalidDataException("Sovereignty hubs require claimable null security space.");
        }
        if (
            action.Set<SavedSovereigntyHub>().Find(system) is not null
            || action.Set<SavedSovereigntyClaim>().Find(system) is not null
            || action
                .Filter<SavedStructure>(s => s.SystemId == system && s.TypeId == EntosisRules.HubTypeId)
                .Any(s => s.State != StructureState.Destroyed && s.State != StructureState.Unanchored)
        )
        {
            throw new InvalidDataException("This system already contains a sovereignty hub.");
        }
        var profile = EntosisCatalog.Hub(catalog);
        var geometry = SystemObjects.Geometry(catalog, system);
        var star =
            geometry.SingleOrDefault(body => body.GroupId == EntosisRules.SunGroup)
            ?? throw new InvalidDataException("The system has no catalog star.");
        var point = EntosisRules.Placement(star.Position, new(position.X, position.Y, position.Z), profile.StarDistance);
        if (
            geometry.Any(body =>
                body.GroupId != EntosisRules.SunGroup && body.Solid && point.DistanceTo(body.Position) < body.Radius + profile.Separation
            )
        )
        {
            throw new InvalidDataException("The sovereignty hub placement is too close to another celestial object.");
        }
        if (
            action
                .Filter<SavedStructure>(row => row.SystemId == system && row.State != StructureState.Destroyed)
                .AsEnumerable()
                .Any(row =>
                    point.DistanceTo(new(row.X, row.Y, row.Z)) < profile.Separation + (catalog.FittingType(row.TypeId)?.Radius ?? 0)
                )
        )
        {
            throw new InvalidDataException("The sovereignty hub placement is too close to an existing structure.");
        }
        long now = Now;
        var structure = new SavedStructure
        {
            Id = Math.Max(InventoryPersistence.NextId(action), StructureRules.FirstStructureId + 1),
            TypeId = item.TypeId,
            Name = catalog.RequireItem(item.TypeId).Name,
            CorporationId = EntosisRules.NeutralCorporationId,
            SystemId = system,
            X = point.X,
            Y = point.Y,
            Z = point.Z,
            State = StructureState.ShieldVulnerable,
            StateStartedAt = now,
            DeployedAt = now,
        };
        var before = PilotInventoryStore.Read(catalog, item);
        item.Quantity--;
        if (item.Quantity == 0)
        {
            action.Remove(item);
        }
        action.Add(structure);
        action.Add(
            new SavedSovereigntyHub
            {
                SystemId = system,
                HubId = structure.Id,
                CaptureDuration = profile.CaptureDuration,
                UpdatedAt = now,
            }
        );
        var result = new StructureResult(
            PilotStructureStore.Snapshot(action, catalog, structure),
            [new(before with { Quantity = item.Quantity }, before)]
        );
        action.Commit();
        return result;
    }

    public SovereigntyHub? Hub(int systemId)
    {
        Advance(systemId, Now);
        return Live.Economy.Inspect(world => Snapshot(world, systemId, Now));
    }

    public IReadOnlyList<SovereigntyClaim> Claims() =>
        Live.Economy.Inspect(world =>
            (IReadOnlyList<SovereigntyClaim>)
                world
                    .Set<SavedSovereigntyClaim>()
                    .OrderBy(row => row.SystemId)
                    .Select(row => new SovereigntyClaim(row.SystemId, row.HubId, row.CorporationId, row.AllianceId, row.ClaimedAt))
                    .ToArray()
        );

    internal static SovereigntyHub? Snapshot(WorldAction world, int system, long now)
    {
        var hub = world.Set<SavedSovereigntyHub>().Find(system);
        if (hub is null || world.Set<SavedStructure>().Find(hub.HubId) is not { State: not StructureState.Destroyed } structure)
        {
            return null;
        }
        var claim = world.Set<SavedSovereigntyClaim>().Find(system);
        return new SovereigntyHub(
            system,
            hub.HubId,
            structure.TypeId,
            structure.CorporationId,
            claim is null ? null : new(claim.SystemId, claim.HubId, claim.CorporationId, claim.AllianceId, claim.ClaimedAt),
            new(
                hub.ControllingAllianceId,
                hub.ProgressTicks,
                hub.UpdatedAt,
                hub.ScoringAllianceId,
                hub.Rate,
                claim?.ClaimedAt,
                hub.Blocked
            ),
            hub.CaptureDuration
        )
        {
            Development = SovereigntyDevelopmentPersistence.Read(world, system, now),
            Conflict = world.Set<SavedSovereigntyConflict>().Find(system) is { } conflict ? Conflict(conflict) : null,
        };
    }

    public IReadOnlyList<EntosisCycle> Cycles(long pilotId) =>
        Live.Economy.Inspect(world =>
            (IReadOnlyList<EntosisCycle>)
                world.Filter<SavedEntosisCycle>(row => row.PilotId == pilotId).OrderBy(row => row.ModuleId).Select(Cycle).ToArray()
        );

    private static EntosisCycle Cycle(SavedEntosisCycle row) =>
        new(
            row.ModuleId,
            row.TypeId,
            row.PilotId,
            row.ShipId,
            row.HubId,
            row.StartedAt,
            row.EndsAt,
            row.WarmupUntil,
            row.SpeedLimit,
            row.Capital,
            row.Repeats,
            row.Stopping
        );

    public bool TravelLocked(long shipId, long now) =>
        Live.Economy.Inspect(world => EntosisPersistence.SpeedLimit(world, shipId, now) is not null);

    public double? SpeedLimit(long shipId, long now) =>
        Live.Economy.Inspect(world =>
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            // Effect 6163 is passive while online. Retain an outstanding paid cycle's
            // limit as well, even if a module disappears before its reservation ends.
            return world
                .Filter<OwnedItem>(row => row.LocationId == shipId && row.Singleton && row.Online)
                .AsEnumerable()
                .Where(row => catalog.FittingType(row.TypeId)?.GroupId == EntosisRules.ModuleGroup)
                .Select(row => (double?)catalog.FittingAttributes(row.TypeId).GetValueOrDefault(EntosisCatalog.SpeedLimitAttribute))
                .Append(EntosisPersistence.SpeedLimit(world, shipId, now))
                .Where(value => value > 0)
                .Min();
        });
}
