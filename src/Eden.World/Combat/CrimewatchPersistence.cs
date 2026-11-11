using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static partial class CrimewatchPersistence
{
    internal static SavedCrimewatch State(WorldDbContext world, long pilotId) =>
        world.Set<SavedCrimewatch>().Find(pilotId) ?? world.Add(new SavedCrimewatch { PilotId = pilotId }).Entity;

    internal static double Security(CatalogDbContext catalog, int systemId, bool required = true)
    {
        double? security = catalog.Read(CatalogData.Client.Systems, (long)systemId)?.SecurityStatus;
        return security is double value && double.IsFinite(value) ? value
            : !required ? 0
            : throw new InvalidDataException("Missing system security classification.");
    }

    internal static int System(CatalogDbContext catalog, Ship ship) =>
        ship.Position?.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId;

    internal static SavedEngagement? Engagement(WorldDbContext world, long pilot, long other) =>
        world.Set<SavedEngagement>().Find(Math.Min(pilot, other), Math.Max(pilot, other));

    internal static AggressionDecision CheckPlayer(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot source,
        Pilot victim,
        int system,
        long now
    )
    {
        var target = world.Set<SavedCrimewatch>().Find(victim.Id)?.Timers() ?? CrimewatchTimers.Empty;
        return CrimewatchRules.Decide(
            new(
                Security(catalog, system),
                source.SafetyLevel,
                world.Set<Account>().Find(source.AccountId)!.CloneGrade == AccountCloneGrade.Alpha,
                catalog.RequireItem(victim.ActiveShip.TypeId).GroupId == FleetDefinitions.CapsuleGroup,
                target.Flag(now),
                (world.Set<SavedSecurityStatus>().Find(victim.Id)?.Value ?? 0) <= CrimewatchRules.OutlawSecurity,
                Engagement(world, source.Id, victim.Id)?.ExpiresAt > now
            )
        );
    }

    internal static void Player(
        WorldDbContext world,
        CatalogDbContext catalog,
        long source,
        long victim,
        int system,
        AggressionDecision decision,
        long now,
        long end
    )
    {
        var attacker = State(world, source);
        var target = State(world, victim);
        attacker.SystemId = target.SystemId = system;
        attacker.Apply(CrimewatchRules.Player(attacker.Timers(), end, decision.Flag, true, now));
        target.Apply(CrimewatchRules.Player(target.Timers(), end, CrimewatchFlag.None, false, now));
        ConcordPersistence.Schedule(world, catalog, world.Pilots.Find(source)!, now);
        long sourceShip = world.Pilots.Find(source)!.ActiveShipId;
        long victimShip = world.Pilots.Find(victim)!.ActiveShipId;
        world.Set<SavedPilotHostility>().Where(h => h.ExpiresAt <= now).ExecuteDelete();
        var hostility =
            world.Set<SavedPilotHostility>().Find(sourceShip, victimShip)
            ?? world.Add(new SavedPilotHostility { SourceShipId = sourceShip, TargetShipId = victimShip }).Entity;
        hostility.ExpiresAt = Math.Max(hostility.ExpiresAt, checked(end + CrimewatchRules.PvpDuration.Ticks));
        hostility.LastAttackAt = Math.Max(hostility.LastAttackAt, now);
        if (decision.Engagement)
        {
            var engagement =
                Engagement(world, source, victim)
                ?? world
                    .Add(new SavedEngagement { LowerPilotId = Math.Min(source, victim), HigherPilotId = Math.Max(source, victim) })
                    .Entity;
            engagement.ActiveUntil = Math.Max(engagement.ActiveUntil, end);
            engagement.ExpiresAt = Math.Max(engagement.ExpiresAt, checked(end + CrimewatchRules.EngagementDuration.Ticks));
        }
        if (world.Set<SavedLogoff>().Find(victim) is { } logoff)
        {
            logoff.ExpiresAt = Math.Max(logoff.ExpiresAt, target.PvpUntil);
        }
    }

    internal static void PlayerImpact(WorldDbContext world, long source, long victim, int system, long now)
    {
        foreach (long pilotId in new[] { source, victim })
        {
            var state = State(world, pilotId);
            state.SystemId = system;
            state.Apply(state.Timers() with { Pvp = CrimewatchRules.Extend(state.Timers().Pvp, now, CrimewatchRules.PvpDuration) });
            if (world.Set<SavedLogoff>().Find(pilotId) is { } logoff)
            {
                logoff.ExpiresAt = Math.Max(logoff.ExpiresAt, state.PvpUntil);
            }
        }
    }

    internal static void Npc(WorldDbContext world, long pilot, int system, long end)
    {
        var state = State(world, pilot);
        state.SystemId = system;
        state.Apply(state.Timers() with { Npc = CrimewatchRules.Extend(state.Timers().Npc, end, CrimewatchRules.NpcDuration) });
    }

    internal static void CheckTravel(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        CrimewatchTravel action,
        long now,
        int? destination = null
    )
    {
        var timers = world.Set<SavedCrimewatch>().Find(pilot.Id)?.Timers() ?? CrimewatchTimers.Empty;
        double security =
            catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId == FleetDefinitions.CapsuleGroup
                ? 0
                : Security(catalog, System(catalog, pilot.ActiveShip), false);
        CrimewatchRules.CheckTravel(timers, action, security, now);
        if (world.Set<SavedConcordResponse>().Any(r => r.ShipId == pilot.ActiveShipId && r.CompletedAt == null))
        {
            throw new InvalidDataException("CONCORD enforcement prevents travel or changing ships.");
        }
    }
}
