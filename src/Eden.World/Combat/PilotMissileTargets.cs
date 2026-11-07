using System.Text.Json;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal sealed partial class PilotMissileStore
{
    private static Pilot? FindVictim(WorldDbContext world, CatalogDbContext catalog, int system, long shipId, long now)
    {
        var victim = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.ActiveShipId == shipId);
        if (
            victim?.ActiveShip.Position is null
            || CrimewatchPersistence.System(catalog, victim.ActiveShip) != system
            || catalog.Set<ItemType>().Find(victim.ActiveShip.TypeId)!.GroupId == FleetDefinitions.CapsuleGroup
            || world.Set<ShipLoss>().Any(l => l.ShipId == shipId)
            || world.Set<SavedLogoff>().Any(l => l.PilotId == victim.Id && l.ExpiresAt <= now)
        )
        {
            return null;
        }
        return victim;
    }

    private static bool EligibleAutomaticTarget(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        long shipId,
        int system,
        long now
    )
    {
        var victim = FindVictim(world, catalog, system, shipId, now);
        if (victim is null || victim.Id == pilot.Id)
        {
            return false;
        }
        try
        {
            _ = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim, system, now);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private sealed record PendingPilotImpact(long AccountId, PilotDamage Damage);

    private PendingPilotImpact ApplyPilotImpact(
        WorldDbContext world,
        CatalogDbContext catalog,
        WorldMissile row,
        MissilePayload payload,
        Pilot victim,
        PilotCombatPresence contact,
        long time
    )
    {
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, victim.Id);
        var defense = ShipHealth.DefenseFrom(attributes);
        double application = MissileRules.Application(
            payload.Warhead,
            attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            contact.Velocity.Length
        );
        var damage = payload.Warhead.Damage.Scale(application);
        var result = PilotHealthStore.Apply(world, row.TargetId, defense, damage, time);
        var attacker = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == row.PilotId);
        var party = PilotInteractionHistoryStore.Player(world, attacker, row.SourceShipId, row.LauncherTypeId);
        PilotInteractionHistoryStore.Damage(world, row.TargetId, party, result.Applied);
        CrimewatchPersistence.PlayerImpact(world, row.PilotId, victim.Id, row.SystemId, time);
        var insurance = result.Remaining.Destroyed
            ? ShipLossPersistence.Apply(
                world,
                catalog,
                victim,
                null,
                row.TargetId,
                row.SystemId,
                victim.ActiveShip.Position!,
                time,
                lootRoll,
                party
            )
            : null;
        ShipHealth health = new(row.TargetId, defense, result.Remaining);
        MissileImpact outcome = new(
            row.Id,
            row.PilotId,
            row.SourceShipId,
            row.TargetId,
            row.LauncherTypeId,
            row.ChargeTypeId,
            health,
            damage,
            result.Applied,
            result.Remaining.Destroyed,
            null
        );
        row.Outcome = JsonSerializer.Serialize(outcome, MissileJson.Default.MissileImpact);
        world.SaveChanges();
        return new(
            victim.AccountId,
            new(victim.Id, row.PilotId, row.SourceShipId, health, damage, result.Applied, 1, row.ChargeTypeId, false, null, insurance)
        );
    }
}
