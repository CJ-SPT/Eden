using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal enum StructureTargetKind
{
    Pilot,
    Npc,
    Structure,
    Drone,
}

internal sealed record StructureTarget(
    long Id,
    int TypeId,
    StructureTargetKind Kind,
    SpacePosition Position,
    SpacePosition Velocity,
    double Radius,
    double SignatureRadius
);

[Injectable]
internal sealed class PilotStructureTargetStore(
    WorldDatabase database,
    TimeProvider clock,
    IPilotStructureControl control,
    IPilotCombatPresence presence
) : IPilotStructureTargeting
{
    public StructureTargetingState Add(StructureControlToken token, long targetId) =>
        Execute(
            token,
            targetId,
            (world, catalog, lease, source, now) =>
            {
                Advance(world, catalog, lease, source, now);
                if (!StructureTargetingRules.Active(source.HasCore, source.State))
                {
                    throw new InvalidDataException("Core and online the structure before acquiring targets.");
                }
                if (targetId == source.Id)
                {
                    throw new InvalidDataException("A structure cannot target itself.");
                }
                var limits = StructureTargetingRules.Limits(source.Fitting!.Attributes);
                var target =
                    Find(world, catalog, source.SystemId, targetId, now) ?? throw new InvalidDataException("The target is unavailable.");
                if (!StructureTargetingRules.InRange(limits, source.Position, source.Profile.Radius, target.Position, target.Radius))
                {
                    throw new InvalidDataException("The target is outside structure targeting range.");
                }
                if (!lease.Targets.ContainsKey(targetId))
                {
                    if (lease.Targets.Count >= limits.MaximumTargets)
                    {
                        throw new InvalidDataException("The structure targeting systems are fully occupied.");
                    }
                    long ready = StructureTargetingRules.ReadyAt(limits, target.SignatureRadius, now);
                    lease.Targets = lease.Targets.Add(targetId, new(targetId, now, ready, false));
                    world.TargetLocks?.Pending(source.Id, targetId);
                }
                return Snapshot(world, lease);
            }
        );

    public StructureTargetingState Read(StructureControlToken token) =>
        Execute(
            token,
            null,
            (world, catalog, lease, source, now) =>
            {
                Advance(world, catalog, lease, source, now);
                return Snapshot(world, lease);
            }
        );

    public StructureTargetingState Remove(StructureControlToken token, long targetId) =>
        Execute(
            token,
            null,
            (world, catalog, lease, source, now) =>
            {
                Drop(world, lease, targetId);
                Advance(world, catalog, lease, source, now);
                return Snapshot(world, lease);
            }
        );

    public StructureTargetingState Clear(StructureControlToken token) =>
        Execute(
            token,
            null,
            (world, _, lease, _, _) =>
            {
                foreach (long id in lease.Targets.Keys.ToArray())
                {
                    Drop(world, lease, id);
                }
                return Snapshot(world, lease);
            }
        );

    public void RequireLocked(StructureControlToken token, long targetId)
    {
        if (!Read(token).Targets.Any(target => target.TargetId == targetId && target.Locked))
        {
            throw new InvalidDataException("Complete a structure target lock before activating defensive equipment.");
        }
    }

    private StructureTargetingState Execute(
        StructureControlToken token,
        long? requested,
        Func<WorldAction, CatalogDbContext, ResidentStructureControl, StructureSnapshot, long, StructureTargetingState> action
    )
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Structure targeting requires the resident world.");
        using var ownership = live.EnterPersistence(nameof(PilotStructureTargetStore));
        var source = control.Require(token);
        if (!live.StructureControllers.TryGetValue(token.StructureId, out var lease) || lease.Token != token)
        {
            throw new InvalidDataException("This structure control lease is no longer active.");
        }
        return lease.WithActive(() =>
        {
            var targets = lease.Targets.Keys.Concat(requested is long id ? [id] : []).Distinct().ToArray();
            using CatalogDbContext catalog = new(database.CatalogPath);
            return live.Economy.Inspect(
                world => action(world, catalog, lease, source, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
                new(
                    systems: [source.SystemId],
                    pilots: [token.PilotId],
                    ships: targets,
                    resources: targets.Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
                )
            );
        });
    }

    private void Advance(WorldAction world, CatalogDbContext catalog, ResidentStructureControl lease, StructureSnapshot source, long now)
    {
        long revision = (world.TargetLocks as IPilotLockBreaks)?.BreakRevision(source.Id) ?? 0;
        bool active = StructureTargetingRules.Active(source.HasCore, source.State) && revision == lease.LockRevision;
        lease.LockRevision = revision;
        var limits = active ? StructureTargetingRules.Limits(source.Fitting!.Attributes) : null;
        int retained = 0;
        foreach (var scan in lease.Targets.Values.OrderBy(scan => scan.StartedAt).ThenBy(scan => scan.TargetId).ToArray())
        {
            var target = active ? Find(world, catalog, source.SystemId, scan.TargetId, now) : null;
            if (
                target is null
                || retained >= limits!.MaximumTargets
                || !StructureTargetingRules.InRange(limits, source.Position, source.Profile.Radius, target.Position, target.Radius)
                || scan.Locked && world.TargetLocks?.Contains(source.Id, scan.TargetId) == false
                || !scan.Locked && world.TargetLocks?.ContainsPending(source.Id, scan.TargetId) == false
            )
            {
                Drop(world, lease, scan.TargetId);
                continue;
            }
            retained++;
            if (!scan.Locked && scan.ReadyAt <= now)
            {
                lease.Targets = lease.Targets.SetItem(scan.TargetId, scan with { Locked = true });
                world.TargetLocks?.Add(source.Id, scan.TargetId);
            }
        }
    }

    private static void Drop(WorldAction world, ResidentStructureControl lease, long target)
    {
        lease.Targets = lease.Targets.Remove(target);
        world.TargetLocks?.Remove(lease.Token.StructureId, target);
    }

    private static StructureTargetingState Snapshot(WorldAction world, ResidentStructureControl lease) =>
        new(
            lease.Token.StructureId,
            lease.Targets.Values.OrderBy(scan => scan.TargetId).ToArray(),
            world.TargetLocks?.Targeters(lease.Token.StructureId) ?? []
        );

    internal StructureTarget? Find(WorldAction world, CatalogDbContext catalog, int system, long id, long now)
    {
        if (world.TetherProtected(id, now))
        {
            return null;
        }
        int type;
        StructureTargetKind kind;
        SpacePosition position;
        SpacePosition velocity = SpacePosition.Zero;
        if (world.Set<WorldNpc>().Find(id) is { } npc)
        {
            if (
                npc.SystemId != system
                || npc.DestroyedAt is not null
                || npc.RetiredAt is not null
                || npc.Navigation == NpcNavigationState.Warping
            )
            {
                return null;
            }
            type = npc.TypeId;
            kind = StructureTargetKind.Npc;
            position = NpcMotion.Position(npc);
            velocity = NpcMotion.Velocity(npc);
        }
        else if (world.Ships.Find(id) is { } ship)
        {
            var pilot = world.Pilots.Find(ship.OwnerId);
            var contact = pilot?.ActiveShipId == id ? presence.Find(system, id) : null;
            if (contact is null || world.Positions.Find(id) is null)
            {
                return null;
            }
            type = ship.TypeId;
            kind = StructureTargetKind.Pilot;
            position = contact.Position;
            velocity = contact.Velocity;
        }
        else if (world.Set<SavedStructure>().Find(id) is { } structure)
        {
            if (structure.SystemId != system || !StructureCombatRules.Vulnerable(structure.State))
            {
                return null;
            }
            type = structure.TypeId;
            kind = StructureTargetKind.Structure;
            position = new(structure.X, structure.Y, structure.Z);
        }
        else if (
            world.Set<WorldDrone>().Find(id) is { SystemId: int droneSystem } drone
            && droneSystem == system
            && world.Set<OwnedItem>().Find(id) is { } item
        )
        {
            type = item.TypeId;
            kind = StructureTargetKind.Drone;
            position = new(drone.X, drone.Y, drone.Z);
            velocity = new(drone.VelocityX, drone.VelocityY, drone.VelocityZ);
        }
        else
        {
            return null;
        }
        return new(
            id,
            type,
            kind,
            position,
            velocity,
            catalog.RequireItem(type).Radius ?? 0,
            PilotFittingStore.SignatureRadius(world, catalog, id, type)
        );
    }
}
