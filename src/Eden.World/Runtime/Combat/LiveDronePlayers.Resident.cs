using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private DronePlayer? ReadDronePlayer(WorldAction world, long shipId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.SingleOrDefault(row => row.ActiveShipId == shipId);
        if (pilot?.ActiveShip.Position is null || world.Set<ShipLoss>().Any(row => row.ShipId == shipId))
        {
            return null;
        }
        var engagements = world
            .Set<SavedEngagement>()
            .Where(row => row.LowerPilotId == pilot.Id || row.HigherPilotId == pilot.Id)
            .AsEnumerable()
            .Select(row => new LimitedEngagement(
                row.LowerPilotId == pilot.Id ? row.HigherPilotId : row.LowerPilotId,
                row.ActiveUntil,
                row.ExpiresAt
            ))
            .ToArray();
        var logoff = world.Set<SavedLogoff>().Find(pilot.Id);
        return new(
            pilot.Id,
            shipId,
            CrimewatchPersistence.System(catalog, pilot.ActiveShip),
            catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId == FleetDefinitions.CapsuleGroup,
            world.Set<Account>().Find(pilot.AccountId)!.CloneGrade == AccountCloneGrade.Alpha,
            pilot.SafetyLevel,
            (world.Set<SavedSecurityStatus>().Find(pilot.Id)?.Value ?? 0) <= CrimewatchRules.OutlawSecurity,
            logoff is null ? null : new(logoff.ShipId, logoff.SystemId, logoff.ExpiresAt),
            engagements,
            world.Set<SavedCrimewatch>().Find(pilot.Id)?.Timers() ?? CrimewatchTimers.Empty
        );
    }

    internal static void ProjectDronePlayers(
        WorldAction world,
        IReadOnlyList<DronePilotTimers> timers,
        IReadOnlyList<DronePilotEngagement> engagements,
        IReadOnlyList<DronePilotHostility> hostilities
    )
    {
        foreach (var state in timers)
        {
            var saved = CrimewatchPersistence.State(world, state.PilotId);
            saved.SystemId = state.SystemId;
            saved.Apply(MergeTimers(saved.Timers(), state.Timers));
            if (world.Set<SavedLogoff>().Find(state.PilotId) is { } logoff)
            {
                logoff.ExpiresAt = Math.Max(logoff.ExpiresAt, state.Timers.Pvp.ExpiresAt);
            }
        }
        foreach (var engagement in engagements)
        {
            var saved =
                CrimewatchPersistence.Engagement(world, engagement.LowerPilotId, engagement.HigherPilotId)
                ?? world
                    .Add(new SavedEngagement { LowerPilotId = engagement.LowerPilotId, HigherPilotId = engagement.HigherPilotId })
                    .Entity;
            saved.ActiveUntil = Math.Max(saved.ActiveUntil, engagement.ActiveUntil);
            saved.ExpiresAt = Math.Max(saved.ExpiresAt, engagement.ExpiresAt);
        }
        foreach (var hostility in hostilities)
        {
            var saved =
                world.Set<SavedPilotHostility>().Find(hostility.SourceShipId, hostility.TargetShipId)
                ?? world
                    .Add(new SavedPilotHostility { SourceShipId = hostility.SourceShipId, TargetShipId = hostility.TargetShipId })
                    .Entity;
            saved.LastAttackAt = Math.Max(saved.LastAttackAt, hostility.LastAttackAt);
            saved.ExpiresAt = Math.Max(saved.ExpiresAt, hostility.ExpiresAt);
        }
    }
}
