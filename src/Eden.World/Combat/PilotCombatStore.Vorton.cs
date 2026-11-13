using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Combat;

internal sealed partial class PilotCombatStore
{
    private sealed record VortonHits(IReadOnlyList<CombatShot> Shots, IReadOnlyList<PilotDamage> Notifications);

    private sealed record VortonTarget(
        long Id,
        double Distance,
        SpacePosition Velocity,
        WorldNpc? Npc = null,
        Pilot? Pilot = null,
        WorldDrone? Drone = null,
        DroneContact? Contact = null
    );

    private VortonHits VortonArcs(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot source,
        long primary,
        TurretTarget target,
        IReadOnlyList<PaidShot> paid,
        AdvancedWeaponCycle cycle,
        long now,
        long end,
        double roll
    )
    {
        var weapon = paid[0].Weapon;
        var profile = weapon.Vorton!;
        int system = target.SystemId;
        double primaryRadius = catalog.RequireItem(target.TypeId).Radius ?? 0;
        double Distance(SpacePosition position, int type) =>
            Math.Max(0, target.Position.DistanceTo(position) - primaryRadius - (catalog.RequireItem(type).Radius ?? 0));
        List<VortonTarget> candidates = [];
        foreach (long id in (cycle.Npcs ?? []).Distinct())
        {
            if (
                id != primary
                && world.Set<WorldNpc>().Find(id) is { DestroyedAt: null, RetiredAt: null } npc
                && npc.SystemId == system
                && npc.Navigation != NpcNavigationState.Warping
            )
            {
                candidates.Add(new(id, Distance(NpcMotion.Position(npc), npc.TypeId), NpcMotion.Velocity(npc), Npc: npc));
            }
        }
        foreach (var ship in (cycle.Ships ?? []).DistinctBy(s => s.Id))
        {
            if (ship.Id == primary || ship.Id == source.ActiveShipId)
            {
                continue;
            }
            var pilot = world.Filter<Pilot>(row => row.ActiveShipId == ship.Id).SingleOrDefault();
            if (
                pilot?.ActiveShip.Position is not null
                && CrimewatchPersistence.System(catalog, pilot.ActiveShip) == system
                && (world.Set<SavedLogoff>().Find(pilot.Id) is not { } logoff || logoff.ExpiresAt > now)
            )
            {
                candidates.Add(
                    new(ship.Id, Distance(ship.Position, pilot.ActiveShip.TypeId), ship.Velocity ?? SpacePosition.Zero, Pilot: pilot)
                );
            }
        }
        foreach (long id in (cycle.Drones ?? []).Distinct())
        {
            if (world.Set<WorldDrone>().Find(id) is { } drone && drone.SystemId == system)
            {
                var contact = PilotDroneStore.Contact(world, catalog, drone);
                if (!contact.Health.Remaining.Destroyed)
                {
                    candidates.Add(
                        new(id, Distance(contact.Position, contact.Profile.TypeId), contact.Velocity, Drone: drone, Contact: contact)
                    );
                }
            }
        }
        List<CombatShot> hits = [];
        List<PilotDamage> notifications = [];
        long sourceShip = source.ActiveShipId;
        var attacker = PilotInteractionHistoryStore.Player(world, source, sourceShip, weapon.TypeId);
        // Start with stable identities so a supplied draw is reproducible in tests.
        var nearby = candidates.Where(c => c.Distance <= profile.ArcRange).OrderBy(c => c.Id).ToArray();
        new Random((int)(roll * int.MaxValue)).Shuffle(nearby);
        foreach (var arc in nearby)
        {
            if (hits.Count >= profile.Targets - 1)
            {
                break;
            }
            IReadOnlyDictionary<DogmaAttributeId, double> attributes;
            Pilot? pilot = arc.Pilot;
            if (pilot is not null)
            {
                attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
                if (PilotHealthStore.Read(world, arc.Id, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed)
                {
                    continue;
                }
            }
            else
            {
                int type = arc.Npc?.TypeId ?? arc.Contact!.Profile.TypeId;
                attributes = arc.Npc is null
                    ? catalog.FittingAttributes(type)
                    : PlayerEwarStore.NpcAttributes(world, arc.Id, catalog.FittingAttributes(type));
            }
            var legalityTarget =
                pilot
                ?? (
                    arc.Drone is { } drone && drone.PilotId != source.Id && drone.ControllerShipId != 0
                        ? world.Pilots.Find(drone.PilotId)
                        : null
                );
            if (legalityTarget is not null)
            {
                AggressionDecision aggression;
                try
                {
                    aggression = CrimewatchPersistence.CheckPlayer(world, catalog, source, legalityTarget, system, now);
                }
                catch (InvalidDataException)
                {
                    continue;
                }
                CrimewatchPersistence.Player(world, catalog, source.Id, legalityTarget.Id, system, aggression, now, end);
            }
            double signature = attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius);
            double application = MissileRules.Application(profile.Warhead, signature, arc.Velocity.Length);
            var damage = paid.Select(p =>
                    p.Weapon.Damage.Scale(MissileRules.Application(p.Weapon.Vorton!.Warhead, signature, arc.Velocity.Length))
                )
                .Aggregate(
                    DamageVector.None,
                    (a, b) => new(a.Em + b.Em, a.Explosive + b.Explosive, a.Kinetic + b.Kinetic, a.Thermal + b.Thermal)
                );
            var appliedWeapon = weapon with { Damage = application > 0 ? damage.Scale(1 / application) : damage };
            if (arc.Npc is { } npc)
            {
                var result = NpcDamagePersistence.Apply(world, catalog, npc, source, sourceShip, attributes, damage, now, weapon.TypeId);
                if (result.Reward is not null)
                {
                    world.SaveChanges();
                }
                hits.Add(
                    new(
                        appliedWeapon,
                        result.Applied,
                        application,
                        result.Health,
                        paid[^1].Capacitor,
                        result.Destroyed,
                        result.Reward is null ? null : NpcDamagePersistence.Read(result.Reward)
                    )
                );
            }
            else if (pilot is not null)
            {
                var defense = ShipHealth.DefenseFrom(attributes);
                var result = PilotHealthStore.Apply(world, arc.Id, defense, damage, now);
                var health = new ShipHealth(arc.Id, defense, result.Remaining);
                DroneCombatPersistence.Incoming(world, arc.Id, sourceShip, now);
                PilotInteractionHistoryStore.Damage(world, arc.Id, attacker, result.Applied);
                var loss = health.Remaining.Destroyed
                    ? losses.Apply(world, catalog, pilot, arc.Id, system, pilot.ActiveShip.Position!, now, attacker)
                    : null;
                if (loss is not null)
                {
                    world.SaveChanges();
                }
                var replacement = loss is null ? null : lookup.FindPilot(world, pilot.AccountId, pilot.Id);
                notifications.Add(
                    new(
                        pilot.Id,
                        source.Id,
                        sourceShip,
                        health,
                        damage,
                        result.Applied,
                        application,
                        weapon.TypeId,
                        paid.Count > 1,
                        replacement,
                        loss?.Insurance
                    )
                    {
                        InventoryChanges = loss?.Changes ?? [],
                    }
                );
                hits.Add(new(appliedWeapon, result.Applied, application, health, paid[^1].Capacitor, health.Remaining.Destroyed, null));
            }
            else
            {
                var result = SmartbombStore.DamageDrone(world, arc.Drone!, arc.Contact!, damage);
                hits.Add(
                    new(
                        appliedWeapon,
                        result.Applied,
                        application,
                        result.Health,
                        paid[^1].Capacitor,
                        result.Health.Remaining.Destroyed,
                        null
                    )
                );
            }
        }
        return new(hits, notifications);
    }
}
