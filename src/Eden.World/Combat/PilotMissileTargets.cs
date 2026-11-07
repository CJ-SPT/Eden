using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
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
            || world.Set<ShipLoss>().Any(l => l.ShipId == shipId)
            || world.Set<SavedLogoff>().Find(victim.Id) is { } logoff && logoff.ExpiresAt <= now
        )
        {
            return null;
        }
        return victim;
    }

    private PilotCombatPresence? FindContact(WorldDbContext world, Pilot pilot, int system, long now)
    {
        long shipId = pilot.ActiveShipId;
        if (presence.FindOnline(system, shipId) is { } online)
        {
            return online;
        }
        // FindVictim has loaded the current logoff row and position under this
        // transaction. Do not reopen a pilot/fitting transaction through presence.
        var offline = world.Set<SavedLogoff>().Find(pilot.Id);
        return
            offline is not null
            && offline.ShipId == shipId
            && offline.SystemId == system
            && offline.ExpiresAt > now
            && pilot.ActiveShip.Position is { } position
            && position.SystemId == system
            ? new(shipId, new(position.X, position.Y, position.Z), SpacePosition.Zero)
            : null;
    }

    private static Pilot? AutomaticVictim(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, long shipId, int system, long now)
    {
        var victim = FindVictim(world, catalog, system, shipId, now);
        if (victim is null || victim.Id == pilot.Id)
        {
            return null;
        }
        try
        {
            _ = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim, system, now);
            return victim;
        }
        catch (InvalidDataException)
        {
            return null;
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
        var loss = result.Remaining.Destroyed
            ? losses.Apply(world, catalog, victim, row.TargetId, row.SystemId, victim.ActiveShip.Position!, time, party)
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
            new(victim.Id, row.PilotId, row.SourceShipId, health, damage, result.Applied, 1, row.ChargeTypeId, false, null, loss?.Insurance)
            {
                InventoryChanges = loss?.Changes ?? [],
            }
        );
    }
}
