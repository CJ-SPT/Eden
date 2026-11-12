using System.Security.Cryptography;
using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

[Injectable]
internal sealed partial class PilotDroneStore(
    WorldDatabase database,
    SecurityStatusSignal security,
    IPilotCombatPresence presence,
    IPilotDamageEvents damageEvents,
    SqliteWorld lookup,
    PilotLossSettlement losses,
    ICooperativeFleets fleets
) : IPilotDrones
{
    private const int ProbabilityResolution = 1_000_000;

    private static Pilot InSpace(WorldDbContext world, long pilotId, bool requireDroneHull = true) =>
        InSpace(new SqlWorldAction(world), pilotId, requireDroneHull);

    private static Pilot InSpace(WorldAction world, long pilotId, bool requireDroneHull = true)
    {
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Choose an existing pilot.");
        if (pilot.ActiveShip.Position is null)
        {
            throw new InvalidDataException("Use drones while your ship is in space.");
        }
        if (requireDroneHull && !DroneRules.SupportsHull(pilot.ActiveShip.TypeId))
        {
            throw new InvalidDataException("This ship does not support combat drone control.");
        }
        return pilot;
    }

    private static void CheckIds(IReadOnlyList<long> ids)
    {
        if (ids.Count < 1 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Choose distinct drones.");
        }
    }

    private static SpacePosition Position(FlightPosition position) => new(position.X, position.Y, position.Z);

    // Ordinary undocks inherit the station's system; explicit IDs are saved by travel.
    private static int CurrentSystem(CatalogDbContext catalog, Pilot pilot) =>
        pilot.ActiveShip.Position?.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;

    private static DroneSettings Settings(WorldAction world, long pilotId) =>
        world.Set<WorldDroneSettings>().Find(pilotId) is { } settings ? new(settings.Aggressive, settings.FocusFire) : new();

    public DroneControl Control(long pilotId)
    {
        if (LiveWorld.Find(database.Path) is { SqlConnections: 0 } live)
        {
            return live.Economy.Inspect(world => ReadControl(world, pilotId), new(pilots: [pilotId]));
        }
        using WorldDbContext world = new(database.Path);
        return ReadControl(new SqlWorldAction(world), pilotId);
    }

    private DroneControl ReadControl(WorldAction world, long pilotId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId)!;
        long ship = pilot.ActiveShipId;
        var fitting = FittingCalculation.Read(world, catalog, pilot);
        double bandwidth = world
            .Filter<WorldDrone>(d => d.PilotId == pilotId && d.ControllerShipId == ship && d.SystemId != null)
            .AsEnumerable()
            .Sum(d => DroneCatalog.Profile(catalog, world.Set<OwnedItem>().Find(d.ItemId)!.TypeId).Bandwidth);
        return new(
            DroneCatalog.ControlLimit(fitting),
            (int)fitting.Graph.Read(pilotId, DogmaAttributeId.DroneControlDistance),
            bandwidth,
            Settings(world, pilotId)
        );
    }

    public void ChangeSettings(long pilotId, bool? aggressive, bool? focusFire)
    {
        using WorldDbContext world = new(database.Path);
        var settings =
            world.Set<WorldDroneSettings>().Find(pilotId) ?? world.Set<WorldDroneSettings>().Add(new() { PilotId = pilotId }).Entity;
        settings.Aggressive = aggressive ?? settings.Aggressive;
        settings.FocusFire = focusFire ?? settings.FocusFire;
        world.SaveChanges();
    }

    public DroneProfile? Profile(long pilotId, int typeId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        try
        {
            return WorldActionScope.Inspect(database, world => DroneCatalog.Skilled(world, catalog, pilotId, typeId));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public DroneOperation Launch(long pilotId, IReadOnlyList<DroneStack> stacks, long now)
    {
        CheckIds(stacks.Select(s => s.Id).ToArray());
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = InSpace(world, pilotId);
        var ship = pilot.ActiveShip;
        var position = ship.Position!;
        var fitting = FittingCalculation.Read(world, catalog, pilot);
        var shipAttributes = fitting.Graph.Attributes(ship.Id);
        if (PilotHealthStore.Read(world, ship.Id, ShipHealth.DefenseFrom(shipAttributes), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot launch drones.");
        }
        var pending = stacks
            .Select(request =>
            {
                var item = InventoryPersistence.OwnedStack(world, pilotId, request.Id);
                if (
                    item is null
                    || item.LocationId != ship.Id
                    || item.FlagId != (int)InventoryFlag.DroneBay
                    || request.Quantity <= 0
                    || request.Quantity > item.Quantity
                )
                {
                    throw new InvalidDataException("Launch owned drones from your active ship's drone bay.");
                }
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
                return (Item: item, Request: request, Profile: DroneCatalog.Skilled(world, catalog, pilotId, item.TypeId));
            })
            .ToArray();
        var active = world
            .Set<WorldDrone>()
            .Where(d => d.ControllerShipId == ship.Id && d.PilotId == pilotId && d.SystemId != null)
            .ToArray();
        double used = active.Sum(d => DroneCatalog.Profile(catalog, world.Set<OwnedItem>().Find(d.ItemId)!.TypeId).Bandwidth);
        DroneRules.CheckLaunch(
            active.Length,
            used,
            pending.Sum(p => p.Request.Quantity),
            pending.Sum(p => p.Profile.Bandwidth * p.Request.Quantity),
            DroneCatalog.ControlLimit(fitting),
            shipAttributes.GetValueOrDefault(DogmaAttributeId.DroneBandwidth)
        );
        List<InventoryChange> changes = [];
        List<DroneLaunch> launches = [];
        int systemId = CurrentSystem(catalog, pilot);
        double launchOffset = (catalog.FittingType(ship.TypeId)?.Radius ?? 0) + DroneRules.LaunchOffset;
        foreach (var (stack, request, profile) in pending)
        {
            var previous = PilotInventoryStore.Read(catalog, stack);
            List<long> ids = [];
            for (int index = 0; index < request.Quantity; index++)
            {
                OwnedItem item;
                InventoryItem? old = null;
                if (stack.Singleton)
                {
                    item = stack;
                    old = PilotInventoryStore.Read(catalog, stack);
                }
                else
                {
                    // Keep the source stack unassembled. Turning its final unit
                    // into a singleton produces an ixQuantity update that the
                    // native inventory cache rejects for negative singleton rows.
                    stack.Quantity--;
                    item = world
                        .Set<OwnedItem>()
                        .Add(
                            new()
                            {
                                Id = InventoryPersistence.NextId(world),
                                OwnerId = pilotId,
                                TypeId = stack.TypeId,
                                Quantity = 1,
                            }
                        )
                        .Entity;
                }
                item.Singleton = true;
                item.LocationId = systemId;
                item.FlagId = (int)InventoryFlag.AutoFit;
                var saved =
                    world.Set<WorldDrone>().Find(item.Id)
                    ?? world
                        .Set<WorldDrone>()
                        .Add(
                            new()
                            {
                                ItemId = item.Id,
                                PilotId = pilotId,
                                Shield = profile.Defense.Shield,
                            }
                        )
                        .Entity;
                saved.PilotId = pilotId;
                DroneDelegationPersistence.Write(saved, null);
                saved.ControllerShipId = ship.Id;
                saved.CorporationId = pilot.CorporationId;
                saved.SystemId = systemId;
                saved.Activity = DroneActivity.Idle;
                saved.TargetId = ship.Id;
                var origin = Position(position) + new SpacePosition(launchOffset + profile.Radius, 0, 0);
                launchOffset += profile.Radius * 2;
                (saved.X, saved.Y, saved.Z) = (origin.X, origin.Y, origin.Z);
                (saved.VelocityX, saved.VelocityY, saved.VelocityZ) = (0, 0, 0);
                saved.UpdatedAt = now;
                saved.CommandedAt = now;
                saved.ShotAt = 0;
                saved.ShotTargetId = 0;
                saved.SpeedLimit = profile.OrbitSpeed;
                saved.Orbiting = true;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), old));
                ids.Add(item.Id);
            }
            if (stack.Quantity == 0)
            {
                world.Set<OwnedItem>().Remove(stack);
                changes.Add(new(previous with { Quantity = 0 }, previous));
            }
            else if (stack.LocationId == ship.Id)
            {
                changes.Add(new(PilotInventoryStore.Read(catalog, stack), previous));
            }
            launches.Add(new(stack.Id, ids));
        }
        world.SaveChanges();
        transaction.Commit();
        return new(changes, launches);
    }

    private static TransactionParticipants OrderParticipants(WorldAction world, long pilot, long? target)
    {
        if (target is not long id)
        {
            return new(pilots: [pilot]);
        }
        return world.Set<WorldNpc>().Find(id) is not null
            ? new(pilots: [pilot], resources: [new(SharedSimulationResource.Npc, id)])
            : new(pilots: [pilot], ships: [id]);
    }

    public void Order(long pilotId, IReadOnlyList<long> drones, DroneOrder order, long? targetId, IReadOnlySet<long> locks, long now)
    {
        CheckIds(drones);
        if (!Enum.IsDefined(order))
        {
            throw new InvalidDataException("Unknown drone command.");
        }
        using WorldActionScope transaction = new(database, select: world => OrderParticipants(world, pilotId, targetId));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = InSpace(world, pilotId);
        var position = pilot.ActiveShip.Position!;
        int systemId = CurrentSystem(catalog, pilot);
        var fitting = FittingCalculation.Read(world, catalog, pilot);
        double range = fitting.Graph.Read(pilotId, DogmaAttributeId.DroneControlDistance);
        double droneRange = order == DroneOrder.Reconnect ? NpcEncounterDefinitions.VisibilityRange : DroneRules.ShutdownDistance;
        var selected = drones.Select(id => world.Set<WorldDrone>().Find(id)).ToArray();
        if (
            selected.Any(d =>
                d is null
                || d.PilotId != pilotId
                || d.SystemId != systemId
                || (order == DroneOrder.Reconnect ? d.ControllerShipId != 0 : d.ControllerShipId != pilot.ActiveShipId)
                || Position(position).DistanceTo(new(d.X, d.Y, d.Z)) > droneRange
            )
        )
        {
            throw new InvalidDataException("These drones are unavailable to your current ship.");
        }
        if (order == DroneOrder.Engage)
        {
            var sourceDefense = ShipHealth.DefenseFrom(fitting.Graph.Attributes(pilot.ActiveShipId));
            if (PilotHealthStore.Read(world, pilot.ActiveShipId, sourceDefense, now).Remaining.Destroyed)
            {
                throw new InvalidDataException("A destroyed ship cannot command drone attacks.");
            }
            var npc = targetId is long id ? world.Set<WorldNpc>().Find(id) : null;
            var victim = npc is null && targetId is long shipId ? FindVictim(world, catalog, pilot, shipId, systemId, now) : null;
            var victimContact = victim is null ? null : FindContact(world, victim, systemId, now);
            if (targetId is not long locked || !locks.Contains(locked))
            {
                throw new InvalidDataException("Order drones to attack a locked ship within drone control range.");
            }
            if (npc is null)
            {
                if (victim is null || victimContact is null || Position(position).DistanceTo(victimContact.Position) > range)
                {
                    throw new InvalidDataException("The target ship is unavailable or outside drone control range.");
                }
                var decision = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim, systemId, now);
                var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, victim.Id));
                if (PilotHealthStore.Read(world, victim.ActiveShipId, defense, now).Remaining.Destroyed)
                {
                    throw new InvalidDataException("The target ship is destroyed.");
                }
                CrimewatchPersistence.Player(world, catalog, pilotId, victim.Id, systemId, decision, now, now);
            }
            else if (
                !locks.Contains(npc.Id)
                || npc.DestroyedAt.HasValue
                || npc.RetiredAt.HasValue
                || npc.Navigation == NpcNavigationState.Warping
                || npc.SystemId != systemId
                || Position(position).DistanceTo(NpcMotion.Position(npc)) > range
            )
            {
                throw new InvalidDataException("Order drones to attack a locked NPC within drone control range.");
            }
            else if (npc.Role == NpcRole.Concord)
            {
                ConcordPersistence.Attack(world, catalog, npc, pilot.ActiveShipId, now);
            }
        }
        if (order == DroneOrder.Reconnect)
        {
            var controlled = world
                .Set<WorldDrone>()
                .Where(d => d.PilotId == pilotId && d.ControllerShipId == pilot.ActiveShipId && d.SystemId != null)
                .ToArray();
            DroneRules.CheckLaunch(
                controlled.Length,
                controlled.Sum(d => DroneCatalog.Profile(catalog, world.Set<OwnedItem>().Find(d.ItemId)!.TypeId).Bandwidth),
                selected.Length,
                selected.Sum(d => DroneCatalog.Profile(catalog, world.Set<OwnedItem>().Find(d!.ItemId)!.TypeId).Bandwidth),
                DroneCatalog.ControlLimit(fitting),
                fitting.Graph.Read(pilot.ActiveShipId, DogmaAttributeId.DroneBandwidth)
            );
        }
        foreach (var entry in selected)
        {
            var drone = entry!;
            if (order == DroneOrder.Abandon)
            {
                Release(drone);
                continue;
            }
            int typeId = world.Set<OwnedItem>().Find(drone.ItemId)!.TypeId;
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, typeId);
            DroneDelegationPersistence.Write(drone, null);
            drone.ControllerShipId = pilot.ActiveShipId;
            drone.CommandedAt = now;
            drone.TargetId = order == DroneOrder.Engage ? targetId!.Value : pilot.ActiveShipId;
            drone.Activity = order switch
            {
                DroneOrder.Engage => DroneActivity.Fighting,
                DroneOrder.ReturnToBay => DroneActivity.ReturningToBay,
                DroneOrder.Orbit => DroneActivity.Returning,
                _ => DroneActivity.Idle,
            };
            drone.Orbiting = false;
            var profile = DroneCatalog.Skilled(world, catalog, pilotId, typeId);
            drone.SpeedLimit = profile.Kind == CombatDroneKind.Sentry ? 0 : profile.MaximumSpeed;
            if (order == DroneOrder.Reconnect)
            {
                drone.UpdatedAt = now;
            }
        }
        world.SaveChanges();
        transaction.Commit();
    }

    internal static DroneContact Contact(WorldDbContext world, CatalogDbContext catalog, WorldDrone drone)
    {
        var profile = DroneCatalog.Skilled(
            world,
            catalog,
            drone.PilotId,
            world.Set<OwnedItem>().Find(drone.ItemId)!.TypeId,
            drone.ControllerShipId
        );
        return new(
            drone.ItemId,
            drone.PilotId,
            drone.ControllerShipId,
            drone.CorporationId,
            drone.SystemId ?? 0,
            profile,
            drone.Activity,
            drone.TargetId,
            new(drone.X, drone.Y, drone.Z),
            new(drone.VelocityX, drone.VelocityY, drone.VelocityZ),
            new(
                drone.ItemId,
                profile.Defense,
                new(
                    Math.Min(profile.Defense.Shield, drone.Shield),
                    Math.Max(0, profile.Defense.Armor - drone.ArmorDamage),
                    Math.Max(0, profile.Defense.Structure - drone.StructureDamage)
                )
            ),
            drone.ShotAt,
            drone.ShotTargetId,
            drone.SpeedLimit,
            drone.Orbiting,
            DroneDelegationPersistence.Read(drone)
        );
    }

    public bool HasControlled(long pilotId, long shipId) =>
        LiveWorld.Find(database.Path)?.CombatForShip(shipId)?.HasControlledDrones(pilotId, shipId)
        ?? ListControllers().Contains((pilotId, shipId));

    private IReadOnlySet<(long Pilot, long Ship)> ListControllers() =>
        LiveReadModels.Find(database.Path)?.DroneControllers(ReadControllers) ?? ReadControllers();

    private IReadOnlySet<(long Pilot, long Ship)> ReadControllers()
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<WorldDrone>()
            .Where(d => d.ControllerShipId != 0 && d.SystemId != null)
            .Select(d => new { d.PilotId, d.ControllerShipId })
            .AsEnumerable()
            .Select(d => (d.PilotId, d.ControllerShipId))
            .ToHashSet();
    }

    public IReadOnlyList<DroneContact> List(int systemId, long now) =>
        LiveWorld.Find(database.Path)?.CombatForSystem(systemId)?.Drones(systemId, now)
        ?? LiveReadModels.Find(database.Path)?.Drones(systemId, () => ReadContacts(systemId))
        ?? ReadContacts(systemId);

    private IReadOnlyList<DroneContact> ReadContacts(int systemId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return world
            .Set<WorldDrone>()
            .Where(d => d.SystemId == systemId && !world.Set<SavedCapsuleerSuspension>().Any(pause => pause.PilotId == d.PilotId))
            .AsEnumerable()
            .Select(d => Contact(world, catalog, d))
            .ToArray();
    }

    public bool WasDestroyed(long droneId)
    {
        // NPC destruction moves the item to the graveyard. Scooping a damaged drone
        // can change its owner's durability skills, but must never play an explosion.
        return WorldActionScope.Inspect(
            database,
            world => world.Set<OwnedItem>().Find(droneId)?.LocationId == (int)InventoryLocation.Graveyard
        );
    }

    public IReadOnlyDictionary<long, ShipHealth> BayHealth(long pilotId, long shipId, IReadOnlySet<long> ids)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var items = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == shipId && i.FlagId == (int)InventoryFlag.DroneBay && ids.Contains(i.Id))
            .ToArray();
        if (items.Length != ids.Count)
        {
            throw new InvalidDataException("Read damage only for your ship's stored drones.");
        }
        return items.ToDictionary(
            i => i.Id,
            i =>
            {
                if (world.Set<WorldDrone>().Find(i.Id) is { } saved)
                {
                    return Contact(world, catalog, saved).Health;
                }
                var defense = ShipHealth.DefenseFrom(catalog.FittingAttributes(i.TypeId));
                return new ShipHealth(i.Id, defense, defense.Undamaged);
            }
        );
    }

    public DroneUpdate Advance(long pilotId, long now, int stamp) =>
        LiveCombatRuntime
            .Find(database.Path, pilotId)
            ?.AdvanceDrones(
                pilotId,
                now,
                stamp,
                presence,
                damageEvents,
                fleets,
                (rolls, endpoints) => SettleAdvance(pilotId, now, stamp, rolls, endpoints)
            )
        ?? SettleAdvance(pilotId, now, stamp);

    private TransactionParticipants AdvanceParticipants(WorldAction world, long pilot, long now, IReadOnlyCollection<long>? endpoints)
    {
        var owner = world.Pilots.Find(pilot) ?? throw new InvalidDataException("Choose an existing pilot.");
        long ship = owner.ActiveShipId;
        var candidates = new HashSet<long>(endpoints ?? []);
        if (endpoints is null)
        {
            var drones = world.Filter<WorldDrone>(row => row.PilotId == pilot && row.ControllerShipId != 0).ToArray();
            candidates.UnionWith(drones.Select(row => row.TargetId));
            foreach (long delegateShip in drones.Select(row => row.DelegateShipId).Where(id => id > 0).Distinct())
            {
                var triggers = DroneCombatPersistence.Read(world, delegateShip);
                candidates.UnionWith([delegateShip, triggers.Offensive.OtherId, triggers.Incoming.OtherId]);
            }
            if (Settings(world, pilot).Aggressive)
            {
                candidates.UnionWith(world.Filter<NpcAggression>(row => row.TargetShipId == ship).Select(row => row.NpcId));
                candidates.UnionWith(
                    world
                        .Filter<SavedPilotHostility>(row => row.TargetShipId == ship && row.ExpiresAt > now)
                        .Select(row => row.SourceShipId)
                );
            }
        }
        HashSet<long> ships = [ship];
        HashSet<long> npcs = [];
        foreach (long id in candidates.Where(id => id > 0 && id != ship))
        {
            if (world.Set<WorldNpc>().Find(id) is not null)
            {
                npcs.UnionWith(NpcHostility.Participants(world, id));
            }
            else
            {
                ships.Add(id);
                npcs.UnionWith(ShipLossPersistence.LossNpcParticipants(world, database.Path, id));
            }
        }
        return new(pilots: [pilot], ships: ships, resources: npcs.Select(id => new SimulationResource(SharedSimulationResource.Npc, id)));
    }

    private DroneUpdate SettleAdvance(
        long pilotId,
        long now,
        int stamp,
        IReadOnlyDictionary<long, double>? rolls = null,
        IReadOnlyCollection<long>? endpoints = null
    )
    {
        using WorldActionScope transaction = new(database, select: world => AdvanceParticipants(world, pilotId, now, endpoints));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Choose an existing pilot.");
        var ship = pilot.ActiveShip;
        int systemId = CurrentSystem(catalog, pilot);
        List<InventoryChange> changes = [];
        List<DroneHit> hits = [];
        List<PilotDamage> pilotHits = [];
        var settings = Settings(world, pilotId);
        var fitting = FittingCalculation.Read(world, catalog, pilot);
        var shipAttributes = fitting.Graph.Attributes(ship.Id);
        int allowedCount = DroneCatalog.ControlLimit(fitting);
        bool sourceDestroyed = PilotHealthStore.Read(world, ship.Id, ShipHealth.DefenseFrom(shipAttributes), now).Remaining.Destroyed;
        double allowedBandwidth = fitting.Graph.Read(ship.Id, DogmaAttributeId.DroneBandwidth);
        double controlRange = fitting.Graph.Read(pilotId, DogmaAttributeId.DroneControlDistance);
        int controlledCount = 0;
        double controlledBandwidth = 0;
        bool released = false;
        foreach (
            var drone in world
                .Set<WorldDrone>()
                .Where(d => d.PilotId == pilotId && d.ControllerShipId != 0 && d.SystemId != null)
                .OrderBy(d => d.ItemId)
                .ToArray()
        )
        {
            if (
                ship.Position is not { } source
                || !DroneRules.SupportsHull(ship.TypeId)
                || sourceDestroyed
                || drone.ControllerShipId != ship.Id
                || systemId != drone.SystemId
                || Position(source).DistanceTo(new(drone.X, drone.Y, drone.Z)) > DroneRules.ShutdownDistance
            )
            {
                Release(drone);
                released = true;
                continue;
            }
            var contact = Contact(world, catalog, drone);
            var profile = contact.Profile;
            bool qualified = true;
            try
            {
                PilotSkillReader.CheckRequirements(world, catalog, pilotId, profile.TypeId);
            }
            catch (InvalidDataException)
            {
                qualified = false;
            }
            if (!qualified || controlledCount >= allowedCount || controlledBandwidth + profile.Bandwidth > allowedBandwidth)
            {
                Release(drone);
                released = true;
                continue;
            }
            controlledCount++;
            controlledBandwidth += profile.Bandwidth;
            var anchor = AdvanceDelegation(world, catalog, pilot, drone, contact, systemId, now);
            contact = contact with
            {
                Activity = drone.Activity,
                TargetId = drone.TargetId,
                Delegation = DroneDelegationPersistence.Read(drone),
            };
            if (now <= drone.UpdatedAt)
            {
                continue;
            }
            // One bounded step per observed interval. Offline/late callbacks never replay combat.
            double seconds = Math.Min(1, (now - drone.UpdatedAt) / (double)TimeSpan.TicksPerSecond);
            if (drone.DelegationMode == DroneDelegationMode.None && drone.Activity == DroneActivity.Idle && settings.Aggressive)
            {
                var attacking = world
                    .Set<NpcAggression>()
                    .Where(a => a.TargetShipId == ship.Id && a.LastShotAt > drone.CommandedAt)
                    .Select(a => a.NpcId)
                    .ToArray();
                var candidates = world
                    .Set<WorldNpc>()
                    .Where(n =>
                        Enumerable.Contains(attacking, n.Id)
                        && n.SystemId == drone.SystemId
                        && n.DestroyedAt == null
                        && n.RetiredAt == null
                        && n.Navigation != NpcNavigationState.Warping
                    )
                    .AsEnumerable()
                    .Where(n => Position(source).DistanceTo(NpcMotion.Position(n)) <= controlRange)
                    .Select(n => n.Id)
                    .Concat(AutomaticTargets(world, catalog, pilot, drone.CommandedAt, systemId, controlRange, now))
                    .Order()
                    .ToArray();
                if (candidates.Length > 0)
                {
                    drone.Activity = DroneActivity.Fighting;
                    drone.TargetId = candidates[settings.FocusFire ? 0 : (int)(drone.ItemId % candidates.Length)];
                }
            }
            var npc = drone.Activity == DroneActivity.Fighting ? world.Set<WorldNpc>().Find(drone.TargetId) : null;
            if (
                npc is not null
                && (
                    npc.DestroyedAt.HasValue
                    || npc.RetiredAt.HasValue
                    || npc.Navigation == NpcNavigationState.Warping
                    || npc.SystemId != drone.SystemId
                    || drone.DelegationMode != DroneDelegationMode.None
                        && Position(source).DistanceTo(NpcMotion.Position(npc)) > controlRange
                )
            )
            {
                npc = null;
            }
            var victim =
                npc is null && drone.Activity == DroneActivity.Fighting
                    ? FindVictim(world, catalog, pilot, drone.TargetId, systemId, now)
                    : null;
            var victimContact = victim is null ? null : FindContact(world, victim, systemId, now);
            if (
                drone.DelegationMode != DroneDelegationMode.None
                && victimContact is not null
                && Position(source).DistanceTo(victimContact.Position) > controlRange
            )
            {
                victimContact = null;
            }
            AggressionDecision? decision = null;
            if (victimContact is not null)
            {
                try
                {
                    decision = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim!, systemId, now);
                }
                catch (InvalidDataException)
                {
                    victimContact = null;
                }
            }
            if (npc is null && victimContact is null && drone.Activity == DroneActivity.Fighting)
            {
                drone.Activity = DroneActivity.Idle;
                drone.TargetId = anchor?.ShipId ?? ship.Id;
            }
            var target = npc is null ? victimContact?.Position ?? anchor?.Position ?? Position(source) : NpcMotion.Position(npc);
            float radius = (float)(
                catalog.FittingType(npc?.TypeId ?? (victimContact is null ? ship.TypeId : victim!.ActiveShip.TypeId))?.Radius ?? 0
            );
            if (npc is null && victimContact is null && anchor is not null)
            {
                radius = (float)anchor.Radius;
            }
            if (
                drone.Activity == DroneActivity.Returning
                && contact.Position.DistanceTo(target) - radius - profile.Radius <= profile.OrbitRange + DroneRules.LaunchOffset
            )
            {
                drone.Activity = DroneActivity.Idle;
            }
            if (
                drone.Activity == DroneActivity.ReturningToBay
                && contact.Position.DistanceTo(target) - radius - profile.Radius <= DroneRules.RecallRange
            )
            {
                double available =
                    shipAttributes.GetValueOrDefault(DogmaAttributeId.DroneCapacity)
                    - InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, (int)InventoryFlag.DroneBay);
                if (available >= profile.Volume)
                {
                    var item = world.Set<OwnedItem>().Find(drone.ItemId)!;
                    var previous = PilotInventoryStore.Read(catalog, item);
                    item.LocationId = ship.Id;
                    item.FlagId = (int)InventoryFlag.DroneBay;
                    drone.SystemId = null;
                    Release(drone);
                    changes.Add(new(PilotInventoryStore.Read(catalog, item), previous));
                    continue;
                }
                drone.Activity = DroneActivity.Idle;
                drone.TargetId = ship.Id;
            }
            contact = DroneRules.Move(
                contact with
                {
                    Activity = drone.Activity,
                    TargetId = drone.TargetId,
                },
                target,
                radius,
                drone.Activity != DroneActivity.ReturningToBay,
                stamp,
                seconds
            );
            (drone.X, drone.Y, drone.Z) = (contact.Position.X, contact.Position.Y, contact.Position.Z);
            (drone.VelocityX, drone.VelocityY, drone.VelocityZ) = (contact.Velocity.X, contact.Velocity.Y, contact.Velocity.Z);
            if (contact.Position.DistanceTo(Position(source)) > DroneRules.ShutdownDistance)
            {
                Release(drone);
                released = true;
                continue;
            }
            drone.SpeedLimit = contact.SpeedLimit;
            drone.Orbiting = contact.Orbiting;
            drone.Shield = ChargedResource.Recharged(
                drone.Shield,
                profile.Defense.Shield,
                profile.Defense.ShieldRechargeMilliseconds,
                seconds * 1000
            );
            drone.UpdatedAt = now;
            if (
                npc is null && victimContact is null
                || drone.ReadyAt > now
                || contact.Position.DistanceTo(target) - radius - profile.Radius > profile.Optimal + profile.Falloff * 2
            )
            {
                continue;
            }
            var attributes = npc is null
                ? PilotFittingStore.ShipAttributes(world, catalog, victim!.Id)
                : PlayerEwarStore.NpcAttributes(world, npc.Id, catalog.FittingAttributes(npc.TypeId));
            var damage = DroneRules.Shot(
                contact,
                target,
                npc is null ? victimContact!.Velocity : NpcMotion.Velocity(npc),
                attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
                rolls is not null && rolls.TryGetValue(drone.ItemId, out double roll)
                    ? roll
                    : RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution
            );
            drone.ReadyAt = checked(now + (long)Math.Ceiling(profile.CycleMilliseconds * TimeSpan.TicksPerMillisecond));
            DroneHit hit;
            if (npc is null)
            {
                CrimewatchPersistence.Player(world, catalog, pilot.Id, victim!.Id, systemId, decision!, now, drone.ReadyAt);
                var impact = ApplyPilotDamage(world, catalog, pilot, victim, drone, profile, attributes, damage, now);
                pilotHits.Add(impact.Damage);
                hit = impact.Hit;
            }
            else
            {
                var result = NpcDamagePersistence.Apply(world, catalog, npc, pilot, ship.Id, attributes, damage, now, profile.TypeId);
                CrimewatchPersistence.Npc(world, pilot.Id, systemId, drone.ReadyAt);
                hit = new(drone.ItemId, profile.TypeId, npc.Id, result.Applied, damage, result.Health, damage.Total / profile.Damage.Total);
            }
            drone.ShotAt = now;
            drone.ShotTargetId = hit.TargetId;
            hits.Add(hit);
        }
        world.SaveChanges();
        transaction.Commit();
        transaction.Dispose();
        foreach (var impact in pilotHits)
        {
            damageEvents.Publish(impact);
        }
        if (hits.Any(h => h.Health.Remaining.Destroyed))
        {
            security.Publish();
        }
        return new(changes, hits, released);
    }

    private static void Release(WorldDrone drone)
    {
        DroneDelegationPersistence.Write(drone, null);
        drone.ControllerShipId = 0;
        drone.TargetId = 0;
        drone.Activity = DroneActivity.Idle;
        drone.Orbiting = false;
        drone.SpeedLimit = 0;
        drone.ShotAt = 0;
        drone.ShotTargetId = 0;
        drone.VelocityX = drone.VelocityY = drone.VelocityZ = 0;
    }

    public void Release(long pilotId, long shipId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        foreach (var drone in world.Set<WorldDrone>().Where(d => d.PilotId == pilotId && d.ControllerShipId == shipId))
        {
            Release(drone);
        }
        transaction.Commit();
    }
}
