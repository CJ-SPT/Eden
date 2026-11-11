using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static partial class ConcordPersistence
{
    internal static SavedConcordResponse? Schedule(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, long now)
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

    internal static void Attack(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc, long shipId, long now)
    {
        var pilot = world.Pilots.SingleOrDefault(p => p.ActiveShipId == shipId);
        if (pilot is null)
        {
            return;
        }
        var decision = CrimewatchRules.Decide(
            new(
                CrimewatchPersistence.Security(catalog, npc.SystemId),
                pilot.SafetyLevel,
                world.Set<Account>().Find(pilot.AccountId)!.CloneGrade == AccountCloneGrade.Alpha,
                false,
                CrimewatchFlag.None,
                false,
                false
            )
        );
        var state = CrimewatchPersistence.State(world, pilot.Id);
        state.SystemId = npc.SystemId;
        state.Apply(CrimewatchRules.Player(state.Timers(), now, decision.Flag, true, now));
        Schedule(world, catalog, pilot, now);
    }

    internal static void Complete(WorldDbContext world, SavedConcordResponse response, long now)
    {
        response.CompletedAt = now;
        // Each response owns only its three responders, including after restart.
        if (response.CommanderId is long commander)
        {
            long[] ids = [commander, response.FirstCaptainId!.Value, response.SecondCaptainId!.Value];
            var squad = world.Set<WorldNpc>().Where(n => ids.Contains(n.Id) && n.Role == NpcRole.Concord).ToArray();
            foreach (var npc in squad)
            {
                npc.RetiredAt = now;
            }
            world.RemoveRange(world.Set<NpcAggression>().Where(a => ids.Contains(a.NpcId)));
        }
    }
}
