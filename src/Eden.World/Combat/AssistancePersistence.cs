using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static class AssistancePersistence
{
    internal static void Apply(WorldDbContext world, CatalogDbContext catalog, Pilot source, Pilot target, long now, long end)
    {
        var timers = world.Set<SavedCrimewatch>().Find(target.Id)?.Timers() ?? CrimewatchTimers.Empty;
        int system = CrimewatchPersistence.System(catalog, source.ActiveShip);
        var flag = AssistanceRules.Decide(
            new(
                CrimewatchPersistence.Security(catalog, system),
                source.SafetyLevel,
                world.Set<Account>().Find(source.AccountId)!.CloneGrade == AccountCloneGrade.Alpha,
                timers.Flag(now),
                (world.Set<SavedSecurityStatus>().Find(target.Id)?.Value ?? 0) <= CrimewatchRules.OutlawSecurity,
                world.Set<SavedEngagement>().Any(e => (e.LowerPilotId == target.Id || e.HigherPilotId == target.Id) && e.ExpiresAt > now),
                timers.Pvp.Running(now)
            )
        );
        var state = CrimewatchPersistence.State(world, source.Id);
        state.SystemId = system;
        state.Apply(AssistanceRules.Apply(state.Timers(), timers, flag, now, end));
        ConcordPersistence.Schedule(world, catalog, source, now);
        if (world.Set<SavedLogoff>().Find(source.Id) is { } logoff)
        {
            logoff.ExpiresAt = Math.Max(logoff.ExpiresAt, Math.Max(state.PvpUntil, state.NpcUntil));
        }
    }
}
