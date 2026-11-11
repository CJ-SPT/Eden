using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static partial class ConcordPersistence
{
    internal static SavedConcordResponse? Schedule(WorldAction world, CatalogDbContext catalog, Pilot pilot, long now)
    {
        var existing = world.Set<SavedConcordResponse>().Find(pilot.ActiveShipId);
        if (existing is not null)
        {
            return existing.CompletedAt is null ? existing : null;
        }
        var ship = world.Ships.Include(s => s.Position).Single(s => s.Id == pilot.ActiveShipId);
        if (ship.Position is null || catalog.RequireItem(ship.TypeId).GroupId == FleetDefinitions.CapsuleGroup)
        {
            return null;
        }
        double security = CrimewatchPersistence.Security(catalog, CrimewatchPersistence.System(catalog, ship), false);
        if (security < CrimewatchRules.HighSecurity || (world.Set<SavedCrimewatch>().Find(pilot.Id)?.CriminalUntil ?? 0) <= now)
        {
            return null;
        }
        // Validate both native profiles before admitting an unpoliced offense.
        foreach (int typeId in new[] { ConcordRules.CaptainType, ConcordRules.CommanderType })
        {
            _ = catalog.RequireItem(typeId);
            var attributes = PilotFittingStore.ReadAttributes(catalog, typeId);
            if (!NpcPopulationRules.HasDirectWeapon(attributes) || ShipHealth.DefenseFrom(attributes).Structure <= 0)
            {
                throw new InvalidDataException("The catalog is missing a CONCORD response profile.");
            }
        }
        long respondAt = checked(now + ConcordRules.ResponseDelay(security).Ticks);
        return world
            .Add(
                new SavedConcordResponse
                {
                    ShipId = ship.Id,
                    PilotId = pilot.Id,
                    SystemId = CrimewatchPersistence.System(catalog, ship),
                    RespondAt = respondAt,
                    DestroyAt = checked(respondAt + ConcordRules.CommanderDelay.Ticks),
                }
            )
            .Entity;
    }
}
