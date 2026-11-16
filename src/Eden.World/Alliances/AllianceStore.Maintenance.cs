using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Alliances;
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
        if (
            !Resident.Economy.Inspect(world =>
                world.Filter<SavedAllianceApplication>(a => a.Status == AllianceApplicationStatus.Accepted && a.EffectiveAt <= now).Any()
            )
        )
        {
            return;
        }
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        foreach (
            var application in world
                .Filter<SavedAllianceApplication>(a => a.Status == AllianceApplicationStatus.Accepted && a.EffectiveAt <= now)
                .ToArray()
        )
        {
            var previous = Application(application);
            var alliance = world.Set<SavedAlliance>().Find(application.AllianceId);
            if (
                alliance is not { ClosedAt: null }
                || world.Set<SavedCorporation>().Find(application.CorporationId) is not { ClosedAt: null }
                || world.Set<SavedAllianceMember>().Find(application.CorporationId) is not null
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
        world.Commit();
        database.NotifyAccessGroupsChanged();
    }
}
