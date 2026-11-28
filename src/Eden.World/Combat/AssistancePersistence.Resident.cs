using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Repair;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.FactionWarfare;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static partial class AssistancePersistence
{
    internal static void Apply(WorldAction world, CatalogDbContext catalog, Pilot source, Pilot target, long now, long end)
    {
        var timers = world.Set<SavedCrimewatch>().Find(target.Id)?.Timers() ?? CrimewatchTimers.Empty;
        int system = CrimewatchPersistence.System(catalog, source.ActiveShip);
        var flag = Check(world, catalog, source, target, now);
        var state = CrimewatchPersistence.State(world, source.Id);
        state.SystemId = system;
        state.Apply(AssistanceRules.Apply(state.Timers(), timers, flag, now, end));
        ConcordPersistence.Schedule(world, catalog, source, now);
        if (world.Set<SavedLogoff>().Find(source.Id) is { } logoff)
        {
            logoff.ExpiresAt = Math.Max(logoff.ExpiresAt, Math.Max(state.PvpUntil, state.NpcUntil));
        }
    }

    internal static AssistanceDecision Check(WorldAction world, CatalogDbContext catalog, Pilot source, Pilot target, long now)
    {
        Eden.World.Sovereignty.EntosisPersistence.RequireAssistance(world, target.ActiveShipId, now);
        var timers = world.Set<SavedCrimewatch>().Find(target.Id)?.Timers() ?? CrimewatchTimers.Empty;
        int system = CrimewatchPersistence.System(catalog, source.ActiveShip);
        double security = CrimewatchPersistence.Security(catalog, system);
        bool targetInCombat = timers.Pvp.Running(now);
        return AssistanceRules.Decide(
            new(
                security,
                source.SafetyLevel,
                world.Set<Account>().Find(source.AccountId)!.CloneGrade == AccountCloneGrade.Alpha,
                timers.Flag(now),
                (world.Set<SavedSecurityStatus>().Find(target.Id)?.Value ?? 0) <= CrimewatchRules.OutlawSecurity,
                security > 0 && IsThirdPartyEngagement(world, source.Id, target.Id, now),
                targetInCombat,
                MilitiaRules.IsThirdPartyCombat(
                    MilitiaEnlistmentStore.Effective(world, source),
                    MilitiaEnlistmentStore.Effective(world, target),
                    targetInCombat
                ),
                security > 0 && IsNeutralWarAssistance(world, source, target, targetInCombat, now)
            )
        );
    }

    private static bool IsThirdPartyEngagement(WorldAction world, long sourceId, long targetId, long now) =>
        world
            .Filter<SavedEngagement>(e => e.LowerPilotId == targetId && e.ExpiresAt > now)
            .AsEnumerable()
            .Select(e => e.HigherPilotId)
            .Concat(world.Filter<SavedEngagement>(e => e.HigherPilotId == targetId && e.ExpiresAt > now).Select(e => e.LowerPilotId))
            .Any(opponent => (CrimewatchPersistence.Engagement(world, sourceId, opponent)?.ExpiresAt ?? 0) <= now);

    internal static bool IsNeutralWarAssistance(WorldAction world, Pilot source, Pilot target, bool targetInCombat, long now)
    {
        if (
            !targetInCombat
            || source.CorporationId == target.CorporationId
            || !CorporationWarStore.ParticipatesInActiveWar(world, target.CorporationId, now)
        )
        {
            return false;
        }
        return CorporationWarStore.Organization(world, source.CorporationId)
            != CorporationWarStore.Organization(world, target.CorporationId);
    }
}
