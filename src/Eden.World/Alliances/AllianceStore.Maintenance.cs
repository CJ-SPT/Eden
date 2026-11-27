using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Alliances;
using Eden.World.Corporations;
using Eden.World.FactionWarfare;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Alliances;

internal sealed partial class AllianceStore
{
    public void Advance()
    {
        if (LiveWorld.Find(database.Path) is null)
        {
            return;
        }
        long now = Now;
        bool billingDue = BillingDeadline() <= now;
        if (
            !billingDue
            && !Resident.Economy.Inspect(world =>
                world.Filter<SavedAllianceApplication>(a => a.Status == AllianceApplicationStatus.Accepted && a.EffectiveAt <= now).Any()
            )
        )
        {
            return;
        }
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var applications = world
            .Filter<SavedAllianceApplication>(a => a.Status == AllianceApplicationStatus.Accepted && a.EffectiveAt <= now)
            .OrderBy(application => application.EffectiveAt)
            .ThenBy(application => application.Id)
            .ToArray();
        bool closed = false;
        foreach (var application in applications)
        {
            if (application.Status != AllianceApplicationStatus.Accepted)
            {
                continue;
            }
            var alliance = world.Set<SavedAlliance>().Find(application.AllianceId);
            // On restart, renew earlier invoices before admission and let later invoices count the admitted corporation.
            if (alliance is { ClosedAt: null } && AdvanceBilling(world, alliance, application.EffectiveAt!.Value, now))
            {
                closed = true;
                continue; // Dissolution already rejected the outstanding applications.
            }
            var previous = Application(application);
            if (
                alliance is not { ClosedAt: null }
                || world.Set<SavedCorporation>().Find(application.CorporationId) is not { ClosedAt: null }
                || world.Set<SavedAllianceMember>().Find(application.CorporationId) is not null
                || CorporationWarStore.PreventsJoiningAlliance(world, application.CorporationId, now)
                || CorporationWarStore.IsAttackingJoiningCorporation(world, application.AllianceId, application.CorporationId, now)
                || MilitiaOrganizationStore.AllianceAdmissionFailure(world, application.CorporationId, application.AllianceId, now)
                    is not null
            )
            {
                application.Status = AllianceApplicationStatus.Rejected;
                application.EffectiveAt = null;
            }
            else
            {
                var before = Snapshot(world, alliance.Id)!;
                var member = world
                    .Add(
                        new SavedAllianceMember
                        {
                            CorporationId = application.CorporationId,
                            AllianceId = application.AllianceId,
                            JoinedAt = application.EffectiveAt!.Value,
                            ChosenExecutorId = alliance.ExecutorCorporationId,
                        }
                    )
                    .Entity;
                application.Status = AllianceApplicationStatus.Effective;
                CorporationWarStore.AdoptDefensiveWars(world, member.CorporationId, alliance.Id, member.JoinedAt);
                if (world.Set<SavedAllianceLifecycle>().Find(alliance.Id) is { } lifecycle)
                {
                    lifecycle.ClosureAt = null;
                }
                world.Add(
                    new SavedAllianceHistory
                    {
                        Id = world.Next("WorldAllianceHistory"),
                        CorporationId = member.CorporationId,
                        AllianceId = member.AllianceId,
                        StartedAt = member.JoinedAt,
                    }
                );
                Record(world, new(0, alliance.Id, member.CorporationId, MemberAfter: Member(member)));
                Recompute(world, alliance.Id);
                Record(world, new(0, alliance.Id, member.CorporationId, Before: before, After: Snapshot(world, alliance.Id)));
            }
            Record(
                world,
                new(
                    0,
                    application.AllianceId,
                    application.CorporationId,
                    ApplicationBefore: previous,
                    ApplicationAfter: Application(application)
                )
            );
        }
        if (billingDue)
        {
            foreach (var alliance in world.Set<SavedAlliance>().Where(alliance => alliance.ClosedAt == null).ToArray())
            {
                closed |= AdvanceBilling(world, alliance, now, now);
            }
        }
        world.Commit();
        if (closed || applications.Length > 0)
        {
            database.NotifyAccessGroupsChanged();
        }
    }
}
