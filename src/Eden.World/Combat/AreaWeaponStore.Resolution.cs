using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Propulsion;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal sealed partial class AreaWeaponStore
{
    private sealed record Target(
        long Id,
        int TypeId,
        SpacePosition Position,
        double Radius,
        IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
        Pilot? Pilot = null,
        WorldNpc? Npc = null,
        WorldDrone? Drone = null
    );

    private TransactionParticipants Participants(int system) =>
        Live.Economy.Inspect(world =>
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            var ships = world
                .Ships.Where(ship => ship.Position != null)
                .AsEnumerable()
                .Where(ship => CrimewatchPersistence.System(catalog, ship) == system)
                .Select(ship => ship.Id)
                .ToArray();
            var pilots = world.Pilots.Where(pilot => Enumerable.Contains(ships, pilot.ActiveShipId)).Select(pilot => pilot.Id).ToArray();
            var npcs = world
                .Filter<WorldNpc>(npc => npc.SystemId == system)
                .Select(npc => new SimulationResource(SharedSimulationResource.Npc, npc.Id))
                .ToArray();
            return new TransactionParticipants(systems: [system], pilots: pilots, ships: ships, resources: npcs);
        });

    private IReadOnlyList<Target> Targets(
        WorldAction world,
        CatalogDbContext catalog,
        int system,
        long now,
        Func<SpacePosition, double, bool>? intersects = null
    )
    {
        List<Target> targets = [];
        foreach (
            var pilot in world
                .Pilots.Where(p => p.ActiveShip.Position != null)
                .AsEnumerable()
                .Where(p =>
                    CrimewatchPersistence.System(catalog, p.ActiveShip) == system
                    && !world.TetherProtected(p.ActiveShipId, now)
                    && !presence.IsAreaProtected(system, p.ActiveShipId)
                )
        )
        {
            if (
                world.Set<ShipLoss>().Find(pilot.ActiveShipId) is not null
                || world.Set<SavedLogoff>().Find(pilot.Id) is { } logoff && logoff.ExpiresAt <= now
            )
            {
                continue;
            }
            var saved = pilot.ActiveShip.Position!;
            var position = presence.FindArea(system, pilot.ActiveShipId)?.Position ?? new(saved.X, saved.Y, saved.Z);
            double radius = catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0;
            if (intersects is not null && !intersects(position, radius))
            {
                continue;
            }
            var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
            if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed)
            {
                continue;
            }
            targets.Add(new(pilot.ActiveShipId, pilot.ActiveShip.TypeId, position, radius, attributes, Pilot: pilot));
        }
        foreach (var npc in world.Filter<WorldNpc>(row => row.SystemId == system && row.DestroyedAt == null && row.RetiredAt == null))
        {
            var position = NpcMotion.Position(npc);
            double radius = catalog.RequireItem(npc.TypeId).Radius ?? 0;
            if (npc.Navigation != NpcNavigationState.Warping && (intersects is null || intersects(position, radius)))
            {
                targets.Add(
                    new(
                        npc.Id,
                        npc.TypeId,
                        position,
                        radius,
                        PilotTackleStore.NpcAttributes(world, npc.Id, catalog.FittingAttributes(npc.TypeId)),
                        Npc: npc
                    )
                );
            }
        }
        foreach (var drone in world.Filter<WorldDrone>(row => row.SystemId == system))
        {
            var item = world.Set<OwnedItem>().Find(drone.ItemId);
            if (item is null)
            {
                continue;
            }
            SpacePosition position = new(drone.X, drone.Y, drone.Z);
            double radius = catalog.RequireItem(item.TypeId).Radius ?? 0;
            if (
                (intersects is null || intersects(position, radius))
                && !PilotDroneStore.Contact(world, catalog, drone).Health.Remaining.Destroyed
            )
            {
                targets.Add(
                    new(
                        drone.ItemId,
                        item.TypeId,
                        position,
                        radius,
                        PlayerEwarStore.NpcAttributes(world, drone.ItemId, catalog.FittingAttributes(item.TypeId)),
                        Drone: drone
                    )
                );
            }
        }
        return targets.OrderBy(target => target.Id).ToArray();
    }

    private static TackleTarget Identity(Target target) =>
        new(
            target.Id,
            target.Npc is not null ? TackleTargetKind.Npc
                : target.Drone is not null ? TackleTargetKind.Drone
                : TackleTargetKind.Ship
        );

    private void Burst(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        AreaWeaponProfile module,
        SpacePosition origin,
        int system,
        long now,
        List<long> broken
    )
    {
        double radius = catalog.RequireItem(source.ActiveShip.TypeId).Radius ?? 0;
        foreach (
            var target in Targets(world, catalog, system, now)
                .Where(target =>
                    target.Id != source.ActiveShipId && SmartbombRules.InRange(origin, radius, target.Position, target.Radius, module.Range)
                )
        )
        {
            Aggression(world, catalog, source, source.ActiveShipId, module.TypeId, target, system, now, now);
            if (Jam(module, target.Attributes))
            {
                Break(world, target, broken);
            }
        }
    }

    private bool Jam(AreaWeaponProfile module, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        ElectronicWarfareModule jammer = new(
            module.ItemId,
            module.TypeId,
            module.Effect,
            NpcEwarKind.Jamming,
            module.Duration,
            module.CapacitorNeed,
            double.MaxValue,
            0,
            module.Power
        );
        return PlayerEwarRules.Attempt(jammer, 0, attributes, rolls.Next()) is not null;
    }

    private static void Break(WorldAction world, Target target, List<long> broken)
    {
        if (target.Pilot is not null)
        {
            broken.Add(target.Id);
        }
        if (target.Npc is not null)
        {
            world.RemoveRange(world.Filter<NpcAggression>(row => row.NpcId == target.Id));
        }
        if (target.Drone is { } drone)
        {
            drone.TargetId = 0;
            drone.Activity = DroneActivity.Idle;
        }
    }

    private static void Aggression(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        long sourceShip,
        int type,
        Target target,
        int system,
        long now,
        long end
    )
    {
        var victim = target.Pilot;
        if (target.Drone is { } drone && drone.PilotId != source.Id)
        {
            victim = world.Pilots.Find(drone.PilotId);
        }
        if (victim is not null && victim.Id != source.Id)
        {
            var decision = CrimewatchPersistence.CheckPlayer(world, catalog, source, victim, system, now);
            CrimewatchPersistence.Player(world, catalog, source.Id, victim.Id, system, decision, now, end);
        }
        if (target.Npc is { } npc)
        {
            CrimewatchPersistence.Npc(world, source.Id, system, end);
            NpcHostility.Record(world, catalog, npc, sourceShip, now);
        }
        if (target.Id != sourceShip)
        {
            DroneCombatPersistence.Module(world, sourceShip, target.Id, victim is not null, now);
        }
    }

    public void Advance(int systemId, long now)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is null)
        {
            return;
        }
        if (!Live.Economy.HasAreaWeapons(systemId))
        {
            return;
        }
        if (
            !Live.Economy.Inspect(world =>
                world
                    .Filter<SavedAreaWeapon>(row => row.SystemId == systemId)
                    .AsEnumerable()
                    .Any(row => row.ResolvesAt <= now || row.Flight() is { Active: true, Module.Kind: AreaWeaponKind.Projector })
            )
        )
        {
            return;
        }
        using var transaction = Live.Economy.Begin(Participants(systemId));
        var world = (WorldAction)transaction;
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<long> broken = [];
        List<PilotDamage> damage = [];
        bool destroyedNpc = false;
        List<long> impacts = [];
        foreach (
            var row in world
                .Filter<SavedAreaWeapon>(row => row.SystemId == systemId)
                .OrderBy(row => row.ResolvesAt)
                .ThenBy(row => row.Id)
                .ToArray()
        )
        {
            // A previous event in this same transaction can destroy a later bomb.
            if (world.Set<SavedAreaWeapon>().Find(row.Id) is null)
            {
                continue;
            }
            var flight = row.Flight();
            if (flight.Module.Kind == AreaWeaponKind.Projector && flight.Active)
            {
                if (now >= row.ResolvesAt)
                {
                    RemoveField(world, row);
                }
                else
                {
                    Project(world, catalog, row, flight, now, broken);
                }
                continue;
            }
            if (now < row.ResolvesAt)
            {
                continue;
            }
            if (flight.Module.Kind == AreaWeaponKind.Doomsday)
            {
                SettleDoomsday(world, catalog, row, flight, now, damage);
            }
            else if (flight.Module.Kind == AreaWeaponKind.Lance)
            {
                destroyedNpc |= SettleLance(world, catalog, row, flight, now, damage);
            }
            else if (flight.Module.Kind == AreaWeaponKind.Defender)
            {
                if (world.Set<SavedAreaWeapon>().Find(flight.TargetId) is { } bomb)
                {
                    world.Remove(bomb);
                }
                world.Remove(row);
            }
            else if (flight.Module.Kind == AreaWeaponKind.Bomb)
            {
                world.Remove(row);
                impacts.Add(row.Id);
                destroyedNpc |= Explode(world, catalog, row, flight, now, broken, damage);
            }
            else
            {
                long expires = AreaWeaponRules.Deadline(row.ResolvesAt, flight.Module.EffectDuration);
                if (expires <= now && flight.Module.Effect != DogmaEffect.ProjectorNeutralizer)
                {
                    RemoveField(world, row);
                    continue;
                }
                var active = flight with { Active = true, ResolvesAt = expires };
                Project(world, catalog, row, active, now, broken);
                if (flight.Module.Effect == DogmaEffect.ProjectorNeutralizer)
                {
                    world.Remove(row);
                }
                else
                {
                    row.Save(active);
                }
            }
        }
        transaction.Commit();
        foreach (long id in impacts)
        {
            _impacts[id] = now + TimeSpan.FromMinutes(2).Ticks;
        }
        foreach (var expired in _impacts.Where(pair => pair.Value <= now).ToArray())
        {
            _impacts.TryRemove(expired.Key, out _);
        }
        foreach (long target in broken)
        {
            locks.BreakOutgoing(target);
        }
        foreach (var notification in damage)
        {
            damageEvents.Publish(notification);
        }
        if (destroyedNpc)
        {
            securityStatus.Publish();
        }
    }

    private static void RemoveField(WorldAction world, SavedAreaWeapon row)
    {
        world.RemoveRange(
            world.Filter<WorldPlayerEwar>(effect =>
                effect.ModuleId == row.ModuleId && AreaWeaponRules.IsProjector(effect.Contact().Module.Effect)
            )
        );
        world.RemoveRange(
            world.Filter<SavedInterdictionField>(field => field.ModuleId == row.ModuleId && field.Kind == InterdictionKind.Projector)
        );
        world.Remove(row);
    }

    public void Cancel(long pilotId, long shipId, long now)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is null)
        {
            return;
        }
        using var transaction = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        var world = (WorldAction)transaction;
        foreach (var row in world.Filter<SavedAreaWeapon>(row => row.PilotId == pilotId && row.SourceShipId == shipId).ToArray())
        {
            if (row.Flight().Module.Kind == AreaWeaponKind.Projector && !row.Flight().Active)
            {
                RemoveField(world, row);
            }
        }
        transaction.Commit();
    }

    public void CancelModule(long pilotId, long shipId, long moduleId)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is null)
        {
            return;
        }
        using var transaction = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        var world = (WorldAction)transaction;
        foreach (
            var row in world
                .Filter<SavedAreaWeapon>(row => row.PilotId == pilotId && row.SourceShipId == shipId && row.ModuleId == moduleId)
                .ToArray()
        )
        {
            if (row.Flight().Module.Kind == AreaWeaponKind.Projector)
            {
                RemoveField(world, row);
            }
        }
        transaction.Commit();
    }
}
