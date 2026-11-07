using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Drones;

internal sealed partial class PilotDroneStore
{
    private static Pilot? FindVictim(WorldDbContext world, CatalogDbContext catalog, Pilot source, long ship, int system, long now)
    {
        var victim = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.ActiveShipId == ship);
        return
            victim is null
            || victim.Id == source.Id
            || victim.ActiveShip.Position is null
            || CrimewatchPersistence.System(catalog, victim.ActiveShip) != system
            || world.Set<ShipLoss>().Any(l => l.ShipId == ship)
            || world.Set<SavedLogoff>().Find(victim.Id) is { } logoff && logoff.ExpiresAt <= now
            ? null
            : victim;
    }

    private PilotCombatPresence? FindContact(WorldDbContext world, Pilot victim, int system, long now)
    {
        if (presence.FindOnline(system, victim.ActiveShipId) is { } online)
        {
            return online;
        }
        var offline = world.Set<SavedLogoff>().Find(victim.Id);
        return
            offline is not null
            && offline.ShipId == victim.ActiveShipId
            && offline.SystemId == system
            && offline.ExpiresAt > now
            && victim.ActiveShip.Position is { } position
            ? new(victim.ActiveShipId, new(position.X, position.Y, position.Z), SpacePosition.Zero)
            : null;
    }

    private IEnumerable<long> AutomaticTargets(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot source,
        long commandedAt,
        int system,
        double range,
        long now
    )
    {
        var attackers = world
            .Set<SavedPilotHostility>()
            .Where(h => h.TargetShipId == source.ActiveShipId && h.ExpiresAt > now && h.LastAttackAt > commandedAt)
            .Select(h => h.SourceShipId)
            .ToArray();
        foreach (long id in attackers)
        {
            var victim = FindVictim(world, catalog, source, id, system, now);
            if (
                victim is null
                || FindContact(world, victim, system, now) is not { } contact
                || Position(source.ActiveShip.Position!).DistanceTo(contact.Position) > range
            )
            {
                continue;
            }
            try
            {
                _ = CrimewatchPersistence.CheckPlayer(world, catalog, source, victim, system, now);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            yield return id;
        }
    }

    private sealed record PendingDroneDamage(long AccountId, DroneHit Hit, PilotDamage Damage);

    private PendingDroneDamage ApplyPilotDamage(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot source,
        Pilot victim,
        WorldDrone drone,
        DroneProfile profile,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        DamageVector damage,
        long now
    )
    {
        long shipId = victim.ActiveShipId;
        var defense = ShipHealth.DefenseFrom(attributes);
        var result = PilotHealthStore.Apply(world, shipId, defense, damage, now);
        var party = PilotInteractionHistoryStore.Player(world, source, source.ActiveShipId, profile.TypeId);
        PilotInteractionHistoryStore.Damage(world, shipId, party, result.Applied);
        var loss = result.Remaining.Destroyed
            ? losses.Apply(world, catalog, victim, shipId, drone.SystemId!.Value, victim.ActiveShip.Position!, now, party)
            : null;
        ShipHealth health = new(shipId, defense, result.Remaining);
        double quality = damage.Total / profile.Damage.Total;
        return new(
            victim.AccountId,
            new(drone.ItemId, profile.TypeId, shipId, result.Applied, damage, health, quality),
            new(
                victim.Id,
                source.Id,
                source.ActiveShipId,
                health,
                damage,
                result.Applied,
                quality,
                profile.TypeId,
                false,
                null,
                loss?.Insurance
            )
            {
                InventoryChanges = loss?.Changes ?? [],
            }
        );
    }

    private void Publish(PendingDroneDamage impact) =>
        damageEvents.Publish(
            impact.Damage with
            {
                Capsule = impact.Damage.Health.Remaining.Destroyed ? lookup.FindPilot(impact.AccountId, impact.Damage.PilotId)! : null,
            }
        );
}
