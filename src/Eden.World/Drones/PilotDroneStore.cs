using System.Security.Cryptography;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
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
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

[Injectable]
internal sealed partial class PilotDroneStore(WorldDatabase database, SecurityStatusSignal security) : IPilotDrones
{
    private const int ProbabilityResolution = 1_000_000;

    private static Pilot InSpace(WorldDbContext world, long pilotId, bool requireDroneHull = true)
    {
        var pilot =
            world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId)
            ?? throw new InvalidDataException("Choose an existing pilot.");
        if (pilot.ActiveShip.Position is null)
        {
            throw new InvalidDataException("Use drones while your ship is in space.");
        }
        if (requireDroneHull && !DroneRules.SupportsHull(pilot.ActiveShip.TypeId))
        {
            throw new InvalidDataException("Light and medium combat drones require a Brutix or Brutix Navy Issue in space.");
        }
        return pilot;
    }

    private static void CheckIds(IReadOnlyList<long> ids)
    {
        if (ids.Count is < 1 or > DroneRules.MaximumDrones || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Choose one to five distinct drones.");
        }
    }

    private static SpacePosition Position(FlightPosition position) => new(position.X, position.Y, position.Z);

    // Ordinary undocks inherit the station's system; explicit IDs are saved by travel.
    private static int CurrentSystem(CatalogDbContext catalog, Pilot pilot) =>
        pilot.ActiveShip.Position?.SystemId
        ?? catalog.Set<Station>().Single(station => station.Id == pilot.ActiveShip.StationId).SolarSystemId;

    private static DroneSettings Settings(WorldDbContext world, long pilotId) =>
        world.Set<WorldDroneSettings>().Find(pilotId) is { } settings ? new(settings.Aggressive, settings.FocusFire) : new();

    public DroneControl Control(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var skills = DroneCatalog.Skills(world, catalog, pilotId);
        long ship = world.Pilots.Single(p => p.Id == pilotId).ActiveShipId;
        double bandwidth = world
            .Set<WorldDrone>()
            .Where(d => d.PilotId == pilotId && d.ControllerShipId == ship && d.SystemId != null)
            .AsEnumerable()
            .Sum(d => DroneCatalog.Profile(catalog, world.Set<OwnedItem>().Find(d.ItemId)!.TypeId).Bandwidth);
        return new(
            Math.Min(DroneRules.MaximumDrones, skills.GetValueOrDefault(DroneSkill.Drones)),
            (int)DroneCatalog.ControlRange(world, catalog, pilotId),
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
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        try
        {
            return DroneCatalog.Skilled(world, catalog, pilotId, typeId);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public DroneOperation Launch(long pilotId, IReadOnlyList<DroneStack> stacks, long now)
    {
        CheckIds(stacks.Select(s => s.Id).ToArray());
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = InSpace(world, pilotId);
        var ship = pilot.ActiveShip;
        var position = ship.Position!;
        var shipAttributes = FittingCalculation.Read(world, catalog, pilot).Graph.Attributes(ship.Id);
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
            DroneCatalog.Skills(world, catalog, pilotId).GetValueOrDefault(DroneSkill.Drones),
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

    public void Order(long pilotId, IReadOnlyList<long> drones, DroneOrder order, long? targetId, IReadOnlySet<long> locks, long now)
    {
        CheckIds(drones);
        if (!Enum.IsDefined(order))
        {
            throw new InvalidDataException("Unknown drone command.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = InSpace(world, pilotId);
        var position = pilot.ActiveShip.Position!;
        int systemId = CurrentSystem(catalog, pilot);
        double range = DroneCatalog.ControlRange(world, catalog, pilotId);
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
            var npc = targetId is long id ? world.Set<WorldNpc>().Find(id) : null;
            if (
                npc is null
                || !locks.Contains(npc.Id)
                || npc.DestroyedAt.HasValue
                || npc.RetiredAt.HasValue
                || npc.Navigation == NpcNavigationState.Warping
                || npc.SystemId != systemId
                || Position(position).DistanceTo(NpcMotion.Position(npc)) > range
            )
            {
                throw new InvalidDataException("Order drones to attack a locked NPC within drone control range.");
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
                DroneCatalog.Skills(world, catalog, pilotId).GetValueOrDefault(DroneSkill.Drones),
                FittingCalculation.Read(world, catalog, pilot).Graph.Read(pilot.ActiveShipId, DogmaAttributeId.DroneBandwidth)
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
            drone.SpeedLimit = DroneCatalog.Skilled(world, catalog, pilotId, typeId).MaximumSpeed;
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
            drone.Orbiting
        );
    }

    public IReadOnlyList<DroneContact> List(int systemId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return world.Set<WorldDrone>().Where(d => d.SystemId == systemId).AsEnumerable().Select(d => Contact(world, catalog, d)).ToArray();
    }

    public bool WasDestroyed(long droneId)
    {
        using WorldDbContext world = new(database.Path);
        // NPC destruction moves the item to the graveyard. Scooping a damaged drone
        // can change its owner's durability skills, but must never play an explosion.
        return world.Set<OwnedItem>().Any(i => i.Id == droneId && i.LocationId == (int)InventoryLocation.Graveyard);
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

    public DroneUpdate Advance(long pilotId, long now, int stamp)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot =
            world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId)
            ?? throw new InvalidDataException("Choose an existing pilot.");
        var ship = pilot.ActiveShip;
        int systemId = CurrentSystem(catalog, pilot);
        List<InventoryChange> changes = [];
        List<DroneHit> hits = [];
        var settings = Settings(world, pilotId);
        var skills = DroneCatalog.Skills(world, catalog, pilotId);
        int allowedCount = Math.Min(DroneRules.MaximumDrones, skills.GetValueOrDefault(DroneSkill.Drones));
        var fitting = FittingCalculation.Read(world, catalog, pilot);
        double allowedBandwidth = fitting.Graph.Read(ship.Id, DogmaAttributeId.DroneBandwidth);
        double controlRange = DroneRules.ControlRange(skills) + fitting.Graph.Read(pilotId, DogmaAttributeId.DroneControlDistance);
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
            if (now <= drone.UpdatedAt)
            {
                continue;
            }
            // One bounded step per observed interval. Offline/late callbacks never replay combat.
            double seconds = Math.Min(1, (now - drone.UpdatedAt) / (double)TimeSpan.TicksPerSecond);
            if (drone.Activity == DroneActivity.Idle && settings.Aggressive)
            {
                var attacking = world
                    .Set<NpcAggression>()
                    .Where(a => a.TargetShipId == ship.Id && a.LastShotAt > drone.CommandedAt)
                    .Select(a => a.NpcId)
                    .ToArray();
                var candidates = world
                    .Set<WorldNpc>()
                    .Where(n =>
                        attacking.Contains(n.Id)
                        && n.SystemId == drone.SystemId
                        && n.DestroyedAt == null
                        && n.RetiredAt == null
                        && n.Navigation != NpcNavigationState.Warping
                    )
                    .AsEnumerable()
                    .Where(n => Position(source).DistanceTo(NpcMotion.Position(n)) <= controlRange)
                    .OrderBy(n => n.Id)
                    .ToArray();
                if (candidates.Length > 0)
                {
                    drone.Activity = DroneActivity.Fighting;
                    drone.TargetId = candidates[settings.FocusFire ? 0 : (int)(drone.ItemId % candidates.Length)].Id;
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
                )
            )
            {
                npc = null;
            }
            if (npc is null && drone.Activity == DroneActivity.Fighting)
            {
                drone.Activity = DroneActivity.Idle;
                drone.TargetId = ship.Id;
            }
            var target = npc is null ? Position(source) : NpcMotion.Position(npc);
            float radius = (float)(catalog.FittingType(npc?.TypeId ?? ship.TypeId)?.Radius ?? 0);
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
                var shipAttributes = FittingCalculation.Read(world, catalog, pilot).Graph.Attributes(ship.Id);
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
            contact = DroneRules.Move(contact, target, radius, drone.Activity != DroneActivity.ReturningToBay, stamp, seconds);
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
                npc is null
                || drone.ReadyAt > now
                || contact.Position.DistanceTo(target) - radius - profile.Radius > profile.Optimal + profile.Falloff * 2
            )
            {
                continue;
            }
            var attributes = catalog.FittingAttributes(npc.TypeId);
            var damage = DroneRules.Shot(
                contact,
                target,
                NpcMotion.Velocity(npc),
                attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
                RandomNumberGenerator.GetInt32(ProbabilityResolution) / (double)ProbabilityResolution
            );
            var result = NpcDamagePersistence.Apply(world, catalog, npc, pilot, ship.Id, attributes, damage, now, profile.TypeId);
            drone.ReadyAt = checked(now + (long)Math.Ceiling(profile.CycleMilliseconds * TimeSpan.TicksPerMillisecond));
            CrimewatchPersistence.Npc(world, pilot.Id, systemId, drone.ReadyAt);
            drone.ShotAt = now;
            drone.ShotTargetId = npc.Id;
            hits.Add(new(drone.ItemId, profile.TypeId, npc.Id, result.Applied, damage, result.Health, damage.Total / profile.Damage.Total));
        }
        world.SaveChanges();
        transaction.Commit();
        if (hits.Any(h => h.Health.Remaining.Destroyed))
        {
            security.Publish();
        }
        return new(changes, hits, released);
    }

    private static void Release(WorldDrone drone)
    {
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
        using WorldDbContext world = new(database.Path);
        foreach (var drone in world.Set<WorldDrone>().Where(d => d.PilotId == pilotId && d.ControllerShipId == shipId))
        {
            Release(drone);
        }
        world.SaveChanges();
    }
}
