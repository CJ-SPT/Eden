using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Structures;

[Injectable]
internal sealed partial class PilotStructureStore(WorldDatabase database, TimeProvider clock) : IPilotStructures, IStructureContacts
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private StructureResult Mutate(
        long pilotId,
        long? structureId,
        long? sourceId,
        Func<WorldDbContext, CatalogDbContext, long, StructureSnapshot> action
    ) =>
        Execute(
            (world, catalog, now) =>
            {
                var before = world
                    .Set<OwnedItem>()
                    .Where(i => i.OwnerId == pilotId || i.LocationId == structureId || i.Id == sourceId)
                    .OrderBy(i => i.Id)
                    .AsEnumerable()
                    .Select(i => PilotInventoryStore.Read(catalog, i))
                    .ToDictionary(i => i.Id);
                var structure = action(world, catalog, now);
                world.SaveChanges();
                var after = world
                    .Set<OwnedItem>()
                    .Where(i => i.OwnerId == pilotId || i.LocationId == structure.Id || i.Id == sourceId)
                    .OrderBy(i => i.Id)
                    .AsEnumerable()
                    .Select(i => PilotInventoryStore.Read(catalog, i))
                    .ToDictionary(i => i.Id);
                var changes = before
                    .Keys.Union(after.Keys)
                    .Order()
                    .Where(id => before.GetValueOrDefault(id) != after.GetValueOrDefault(id))
                    .Select(id => new InventoryChange(
                        after.GetValueOrDefault(id) ?? before[id] with { Quantity = 0 },
                        before.GetValueOrDefault(id)
                    ))
                    .ToArray();
                return new StructureResult(structure, changes);
            }
        );

    private T Execute<T>(Func<WorldDbContext, CatalogDbContext, long, T> action)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        Advance(world, catalog, now);
        var result = action(world, catalog, now);
        world.SaveChanges();
        transaction.Commit();
        return result;
    }

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == id);

    internal static SavedStructure Require(WorldDbContext world, long id) =>
        world.Set<SavedStructure>().Find(id) is { State: not StructureState.Destroyed } structure
            ? structure
            : throw new InvalidDataException("The structure is unavailable.");

    private static Pilot Manage(WorldDbContext world, SavedStructure structure, long pilotId)
    {
        var pilot = Pilot(world, pilotId);
        var corporation = world.Set<SavedCorporation>().Find(structure.CorporationId);
        if (
            corporation is null
            || pilot.CorporationId != corporation.Id
            || !CorporationRoleStore.Effective(world, pilotId, corporation).Roles.Has(CorporationRole.StationManager)
        )
        {
            throw new InvalidDataException("Your corporation's Station Manager or Director authority is required.");
        }
        return pilot;
    }

    internal static bool Access(WorldDbContext world, SavedStructure structure, long pilotId)
    {
        var pilot = world.Pilots.Find(pilotId);
        if (pilot is null)
        {
            return false;
        }
        var entries = world.Set<SavedStructureAccess>().Where(a => a.StructureId == structure.Id).ToArray();
        if (entries.FirstOrDefault(a => a.MemberId == pilotId) is { } individual)
        {
            return individual.Allowed;
        }
        if (entries.FirstOrDefault(a => a.MemberId == pilot.CorporationId) is { } corporation)
        {
            return corporation.Allowed;
        }
        return pilot.CorporationId == structure.CorporationId || structure.PublicAccess;
    }

    internal static StructureSnapshot Snapshot(WorldDbContext world, CatalogDbContext catalog, SavedStructure structure)
    {
        var modules = world
            .Set<SavedStructureService>()
            .Where(m => m.StructureId == structure.Id)
            .OrderBy(m => m.Slot)
            .AsEnumerable()
            .Select(m => new StructureModule(m.ItemId, world.Set<OwnedItem>().Find(m.ItemId)!.TypeId, m.Slot, m.Online, m.NextFuelAt))
            .ToArray();
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        HashSet<StructureService> services = [];
        if (profile.Dockable && StructureRules.CanDock(profile, structure.State, 0))
        {
            services.UnionWith([StructureService.Docking, StructureService.Insurance]);
            if (structure.HasCore)
            {
                services.UnionWith([StructureService.Fitting, StructureService.Repair]);
            }
        }
        if (StructureRules.Operational(structure.State))
        {
            foreach (var module in modules.Where(m => m.Online))
            {
                services.UnionWith(StructureCatalog.Service(catalog, module.TypeId).Services.Where(StructureRules.ServiceImplemented));
            }
        }
        if (
            services.Overlaps([
                StructureService.BasicManufacturing,
                StructureService.CapitalManufacturing,
                StructureService.SupercapitalManufacturing,
            ])
        )
        {
            services.UnionWith([StructureService.Manufacturing, StructureService.Industry]);
        }
        if (
            services.Overlaps([
                StructureService.TimeResearch,
                StructureService.MaterialResearch,
                StructureService.Copying,
                StructureService.Invention,
            ])
        )
        {
            services.UnionWith([StructureService.Laboratory, StructureService.Industry]);
        }
        if (
            services.Overlaps([
                StructureService.CompositeReactions,
                StructureService.HybridReactions,
                StructureService.BiochemicalReactions,
            ])
        )
        {
            services.UnionWith([StructureService.Reactions, StructureService.Industry]);
        }
        return new(
            structure.Id,
            profile,
            structure.Name,
            structure.Description,
            structure.CorporationId,
            structure.SystemId,
            new(structure.X, structure.Y, structure.Z),
            structure.Yaw,
            structure.State,
            structure.StateEndsAt,
            structure.UnanchorAt,
            structure.HasCore,
            structure.PublicAccess,
            structure.LowPowerAt,
            modules,
            services.Order().ToArray(),
            structure.MoonId,
            structure.StateStartedAt,
            structure.ReinforceWeekday,
            structure.ReinforceHour
        );
    }

    public IReadOnlyList<StructureSnapshot> List(long pilotId, int? systemId = null) =>
        Execute(
            (world, catalog, _) =>
                world
                    .Set<SavedStructure>()
                    .Where(s => s.State != StructureState.Destroyed && (!systemId.HasValue || s.SystemId == systemId))
                    .OrderBy(s => s.Id)
                    .AsEnumerable()
                    .Select(s => Snapshot(world, catalog, s))
                    .ToArray()
        );

    public StructureSnapshot? Read(long pilotId, long structureId) =>
        Execute(
            (world, catalog, _) =>
                world.Set<SavedStructure>().Find(structureId) is { State: not StructureState.Destroyed } s
                    ? Snapshot(world, catalog, s)
                    : null
        );

    public bool HasService(long pilotId, long structureId, StructureService service) =>
        Execute(
            (world, catalog, _) =>
                world.Set<SavedStructure>().Find(structureId) is { } structure
                && Access(world, structure, pilotId)
                && Snapshot(world, catalog, structure).Services.Contains(service)
        );

    public IReadOnlyList<InventoryItem> Inventory(long pilotId, long structureId) =>
        Execute(
            (world, catalog, _) =>
            {
                var structure = Require(world, structureId);
                Manage(world, structure, pilotId);
                return world
                    .Set<OwnedItem>()
                    .Where(i => i.LocationId == structureId && i.OwnerId == structure.CorporationId)
                    .OrderBy(i => i.Id)
                    .AsEnumerable()
                    .Select(i => PilotInventoryStore.Read(catalog, i))
                    .ToArray();
            }
        );

    public StructureResult Deploy(long pilotId, StructureDeployment deployment) =>
        Mutate(
            pilotId,
            null,
            deployment.ItemId,
            (world, catalog, now) =>
            {
                var pilot = Pilot(world, pilotId);
                var corporation =
                    world.Set<SavedCorporation>().Find(pilot.CorporationId)
                    ?? throw new InvalidDataException("Only a player corporation may deploy Upwell structures.");
                var authority = CorporationRoleStore.Effective(world, pilotId, corporation).Roles;
                if (!authority.Has(CorporationRole.StationManager) || corporation.CreatedAt > now - 7 * TimeSpan.TicksPerDay)
                {
                    throw new InvalidDataException("A seven-day-old player corporation and structure management authority are required.");
                }
                var ship = pilot.ActiveShip;
                var position = ship.Position ?? throw new InvalidDataException("Deploy from a ship in space.");
                var item = world.Set<OwnedItem>().Find(deployment.ItemId);
                if (
                    item is null
                    || item.OwnerId != pilotId
                    || item.LocationId != ship.Id
                    || item.FlagId != (int)InventoryFlag.Cargo
                    || item.Quantity < 1
                    || item.Singleton
                )
                {
                    throw new InvalidDataException("The packaged structure must be in your active ship's cargo.");
                }
                var profile = StructureCatalog.Require(catalog, item.TypeId);
                int system = position.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId;
                double security = catalog.Definitions.SystemSecurity.GetValueOrDefault(system);
                if (
                    (profile.DisallowHighSecurity || !profile.Dockable) && security >= StructureRules.HighSecurityBoundary
                    || system is 30000142 or 30002187 or 30002659 or 30002510 or 30002053
                )
                {
                    throw new InvalidDataException("This structure cannot be deployed in this system.");
                }
                var point = deployment.Position;
                if (
                    !double.IsFinite(point.X)
                    || !double.IsFinite(point.Y)
                    || !double.IsFinite(point.Z)
                    || !double.IsFinite(deployment.Yaw)
                    || point.DistanceTo(new(position.X, position.Y, position.Z)) > StructureRules.PlacementRange
                )
                {
                    throw new InvalidDataException("Choose a finite deployment position within range of your ship.");
                }
                foreach (var obstacle in SystemObjects.Geometry(catalog, system))
                {
                    if (point.DistanceTo(obstacle.Position) < obstacle.Radius + profile.Radius + StructureRules.Separation)
                    {
                        throw new InvalidDataException("The deployment position is too close to a celestial, gate or station.");
                    }
                }
                if (
                    world
                        .Set<SavedStructure>()
                        .Where(s => s.SystemId == system && s.State != StructureState.Destroyed)
                        .AsEnumerable()
                        .Any(s =>
                            point.DistanceTo(new(s.X, s.Y, s.Z))
                            < StructureRules.Separation + profile.Radius + StructureCatalog.Require(catalog, s.TypeId).Radius
                        )
                )
                {
                    throw new InvalidDataException("The deployment position is too close to another structure.");
                }
                string name = StructureRules.Name(deployment.Name);
                if (deployment.ReinforceWeekday is < 0 or > 6 || deployment.ReinforceHour is < 0 or > 23)
                {
                    throw new InvalidDataException("Choose a valid reinforcement day and UTC hour.");
                }
                if (deployment.Description.Length > StructureRules.MaximumDescriptionLength)
                {
                    throw new InvalidDataException("The structure description is too long.");
                }
                SavedStructure structure = new()
                {
                    Id = Math.Max(InventoryPersistence.NextId(world), StructureRules.FirstStructureId + 1),
                    TypeId = item.TypeId,
                    Name = name,
                    Description = deployment.Description,
                    CorporationId = corporation.Id,
                    SystemId = system,
                    MoonId = deployment.MoonId,
                    ReinforceWeekday = deployment.ReinforceWeekday,
                    ReinforceHour = deployment.ReinforceHour,
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z,
                    Yaw = deployment.Yaw,
                    State = StructureState.Anchoring,
                    StateEndsAt = checked(
                        now + StructureRules.Duration(StructureRules.AnchorDuration(profile), world.Settings.Structures.AnchoringSpeed)
                    ),
                    DeployedAt = now,
                    LowPowerAt = now,
                    StateStartedAt = now,
                    HasCore = profile.BuiltinCoreTypeId.HasValue,
                };
                world.Add(structure);
                Consume(world, item, 1);
                world.SaveChanges();
                if (profile.BuiltinCoreTypeId is int builtinCore)
                {
                    InventoryPersistence.MergeOutput(
                        world,
                        catalog,
                        structure.CorporationId,
                        structure.Id,
                        StructureRules.CoreRoomFlag,
                        builtinCore,
                        1
                    );
                    world.SaveChanges();
                }
                if (profile.BuiltinModuleTypeId is int builtin)
                {
                    var module = new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        TypeId = builtin,
                        OwnerId = corporation.Id,
                        LocationId = structure.Id,
                        FlagId = StructureRules.FirstServiceSlot,
                        Quantity = 1,
                        Singleton = true,
                    };
                    world.Add(module);
                    world.Add(
                        new SavedStructureService
                        {
                            ItemId = module.Id,
                            StructureId = structure.Id,
                            Slot = module.FlagId,
                        }
                    );
                    world.SaveChanges();
                }
                return Snapshot(world, catalog, structure);
            }
        );

    private StructureResult Update(
        long pilotId,
        long structureId,
        Action<WorldDbContext, CatalogDbContext, SavedStructure, Pilot, long> action
    ) =>
        Mutate(
            pilotId,
            structureId,
            null,
            (world, catalog, now) =>
            {
                var structure = Require(world, structureId);
                var pilot = Manage(world, structure, pilotId);
                action(world, catalog, structure, pilot, now);
                world.SaveChanges();
                return Snapshot(world, catalog, structure);
            }
        );

    public StructureResult Rename(long pilotId, long structureId, string name) =>
        Update(pilotId, structureId, (_, _, structure, _, _) => structure.Name = StructureRules.Name(name));

    public StructureResult Describe(long pilotId, long structureId, string description) =>
        Update(
            pilotId,
            structureId,
            (_, _, structure, _, _) =>
                structure.Description =
                    description.Length <= StructureRules.MaximumDescriptionLength
                        ? description
                        : throw new InvalidDataException("Description too long.")
        );

    public StructureResult SetAccess(long pilotId, long structureId, bool publicAccess, IReadOnlyList<StructureAccess> members) =>
        Update(
            pilotId,
            structureId,
            (world, _, structure, _, _) =>
            {
                if (members.Any(m => m.MemberId <= 0) || members.DistinctBy(m => m.MemberId).Count() != members.Count)
                {
                    throw new InvalidDataException("Choose distinct positive pilot or corporation access identities.");
                }
                structure.PublicAccess = publicAccess;
                world.RemoveRange(world.Set<SavedStructureAccess>().Where(a => a.StructureId == structureId));
                world.SaveChanges();
                world.AddRange(
                    members.Select(m => new SavedStructureAccess
                    {
                        StructureId = structureId,
                        MemberId = m.MemberId,
                        Allowed = m.Allowed,
                    })
                );
            }
        );

    private static OwnedItem AccessibleItem(WorldDbContext world, Pilot pilot, SavedStructure structure, long itemId)
    {
        var item = world.Set<OwnedItem>().Find(itemId);
        bool docked = pilot.ActiveShip.Position is null && pilot.ActiveShip.StructureId == structure.Id;
        bool nearby =
            pilot.ActiveShip.Position is { SystemId: int system } p
            && system == structure.SystemId
            && new SpacePosition(p.X, p.Y, p.Z).DistanceTo(new(structure.X, structure.Y, structure.Z)) <= StructureCatalogRange;
        if (
            item is null
            || item.OwnerId != pilot.Id
            || item.Quantity <= 0
            || (!docked && !nearby)
            || !(
                item.LocationId == pilot.ActiveShipId && item.FlagId == (int)InventoryFlag.Cargo
                || docked && item.LocationId == structure.Id && item.FlagId == (int)InventoryFlag.Hangar
            )
        )
        {
            throw new InvalidDataException("Use an owned item in your docked hangar or nearby active ship's cargo.");
        }
        return item;
    }

    private const double StructureCatalogRange = 250_000;

    private static void Consume(WorldDbContext world, OwnedItem item, int quantity)
    {
        if (quantity <= 0 || quantity > item.Quantity)
        {
            throw new InvalidDataException("The requested quantity is unavailable.");
        }
        item.Quantity -= quantity;
        if (item.Quantity == 0)
        {
            world.Remove(item);
        }
    }

    public StructureResult InstallCore(long pilotId, long structureId, long itemId) =>
        Update(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, now) =>
            {
                var item = AccessibleItem(world, pilot, structure, itemId);
                if (
                    structure.HasCore
                    || structure.State != StructureState.Onlining
                    || StructureCatalog.Require(catalog, structure.TypeId).CoreTypeId != item.TypeId
                )
                {
                    throw new InvalidDataException("This structure is not awaiting this quantum core.");
                }
                Consume(world, item, 1);
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    structure.CorporationId,
                    structure.Id,
                    StructureRules.CoreRoomFlag,
                    item.TypeId,
                    1
                );
                structure.HasCore = true;
                structure.StateStartedAt = now;
                structure.StateEndsAt = checked(
                    now + StructureRules.Duration(StructureRules.RepairDuration, world.Settings.Structures.OnliningSpeed)
                );
            }
        );

    public StructureResult Unanchor(long pilotId, long structureId, bool cancel = false) =>
        Update(
            pilotId,
            structureId,
            (world, catalog, structure, _, now) =>
            {
                if (cancel)
                {
                    structure.UnanchorAt = null;
                }
                else if (structure.State != StructureState.ShieldVulnerable || structure.UnanchorAt.HasValue)
                {
                    throw new InvalidDataException("The structure cannot begin decommissioning in its current state.");
                }
                else
                {
                    structure.UnanchorAt = checked(
                        now
                        + StructureRules.Duration(
                            StructureRules.UnanchorDuration(StructureCatalog.Require(catalog, structure.TypeId)),
                            world.Settings.Structures.UnanchoringSpeed
                        )
                    );
                }
            }
        );

    public void Advance() => Execute((_, _, _) => true);

    public IReadOnlyList<StaticSpaceObject> Contacts(int systemId)
    {
        var reads = Eden.World.Runtime.LiveReadModels.Find(database.Path);
        return (
            reads?.Structures(systemId, database.Catalog.Identity.Generation, Now, () => LoadContacts(systemId)) ?? LoadContacts(systemId)
        ).Contacts;
    }

    private Eden.World.Runtime.StructureContactView LoadContacts(int systemId) =>
        Execute(
            (world, catalog, _) =>
            {
                var structures = world
                    .Set<SavedStructure>()
                    .Where(s => s.SystemId == systemId && s.State != StructureState.Destroyed)
                    .OrderBy(s => s.Id)
                    .ToArray();
                var contacts = structures
                    .Select(s => new StaticSpaceObject(
                        s.Id,
                        s.TypeId,
                        StructureCatalog.Require(catalog, s.TypeId).GroupId,
                        StructureRules.CategoryId,
                        s.CorporationId,
                        new(s.X, s.Y, s.Z),
                        (float)StructureCatalog.Require(catalog, s.TypeId).Radius
                    )
                    {
                        Name = s.Name,
                        Structure = new(s.State, s.StateStartedAt, s.StateEndsAt, s.UnanchorAt, s.HasCore),
                    })
                    .ToArray();
                long deadline = structures
                    .SelectMany(s => new[] { s.StateEndsAt, s.UnanchorAt })
                    .Where(d => d.HasValue && d.Value > Now)
                    .Select(d => d!.Value)
                    .DefaultIfEmpty(long.MaxValue)
                    .Min();
                return new Eden.World.Runtime.StructureContactView(
                    Array.AsReadOnly(contacts),
                    deadline,
                    structures.Any(s => s.UnanchorAt <= Now)
                );
            }
        );
}
