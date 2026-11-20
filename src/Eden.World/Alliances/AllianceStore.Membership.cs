using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Alliances;
using Eden.World.Corporations;
using Eden.World.FactionWarfare;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Alliances;

internal sealed partial class AllianceStore
{
    public long Head => LiveWorld.Find(database.Path)?.Economy.Inspect(_ => Resident.Economy.Sequence("WorldAllianceChange")) ?? 0;

    public AllianceApplication Apply(long actorId, int allianceId, string text)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var result = Apply(world, actorId, allianceId, text, Now);
        world.Commit();
        return result;
    }

    internal static AllianceApplication Apply(WorldAction world, long actorId, int allianceId, string text, long now)
    {
        AllianceMembershipRules.ValidateApplication(text);
        var corporation = CorporationRoleStore.Current(world, actorId);
        CorporationRoleStore.RequireDirector(world, actorId, corporation);
        _ = Active(world, allianceId);
        if (world.Set<SavedAllianceMember>().Find(corporation.Id) is not null)
        {
            throw new InvalidDataException("Leave your current alliance before applying.");
        }
        if (MilitiaOrganizationStore.AllianceAdmissionFailure(world, corporation.Id, allianceId, now) is { } failure)
        {
            throw new InvalidDataException(failure);
        }
        var applications = world.Filter<SavedAllianceApplication>(a => a.CorporationId == corporation.Id).ToArray();
        if (
            applications.Any(a =>
                a.AllianceId == allianceId && a.Status is AllianceApplicationStatus.New or AllianceApplicationStatus.Accepted
            )
        )
        {
            throw new InvalidDataException("Your corporation already has an application to this alliance.");
        }
        foreach (var previous in applications.Where(a => a.Status is AllianceApplicationStatus.New or AllianceApplicationStatus.Accepted))
        {
            Record(world, new(0, previous.AllianceId, corporation.Id, ApplicationBefore: Application(previous)));
            world.Remove(previous);
        }
        var row = applications.SingleOrDefault(a => a.AllianceId == allianceId);
        AllianceApplication? before = row is null ? null : Application(row);
        row ??= world
            .Add(
                new SavedAllianceApplication
                {
                    Id = world.Next("WorldAllianceApplication"),
                    CorporationId = corporation.Id,
                    AllianceId = allianceId,
                }
            )
            .Entity;
        row.Text = text;
        row.Status = AllianceApplicationStatus.New;
        row.AppliedAt = now;
        row.EffectiveAt = null;
        var after = Application(row);
        Record(world, new(0, allianceId, corporation.Id, ApplicationBefore: before, ApplicationAfter: after));
        return after;
    }

    public void Withdraw(long actorId, int allianceId)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        CorporationRoleStore.RequireDirector(world, actorId, corporation);
        var row =
            world.Filter<SavedAllianceApplication>(a => a.CorporationId == corporation.Id && a.AllianceId == allianceId).SingleOrDefault()
            ?? throw new InvalidDataException("Unknown alliance application.");
        if (row.Status == AllianceApplicationStatus.Effective)
        {
            throw new InvalidDataException("An effective membership must be left through the member list.");
        }
        Record(world, new(0, allianceId, corporation.Id, ApplicationBefore: Application(row)));
        world.Remove(row);
        world.Commit();
    }

    public void Decide(long actorId, int corporationId, string text, AllianceApplicationStatus status)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        if (Decide(world, actorId, corporationId, text, status, Now))
        {
            world.Commit();
        }
    }

    internal static bool Decide(WorldAction world, long actorId, int corporationId, string text, AllianceApplicationStatus status, long now)
    {
        AllianceMembershipRules.ValidateApplication(text);
        ValidateDecision(status);
        var alliance = Executor(world, actorId);
        var row =
            world.Filter<SavedAllianceApplication>(a => a.AllianceId == alliance.Id && a.CorporationId == corporationId).SingleOrDefault()
            ?? throw new InvalidDataException("Unknown alliance application.");
        if (row.Status == status)
        {
            return false;
        }
        if (
            row.Status != AllianceApplicationStatus.New
            || world.Set<SavedAllianceMember>().Find(corporationId) is not null
            || world.Set<SavedCorporation>().Find(corporationId) is not { ClosedAt: null }
        )
        {
            throw new InvalidDataException("This application is no longer eligible.");
        }
        if (
            status == AllianceApplicationStatus.Accepted
            && MilitiaOrganizationStore.AllianceAdmissionFailure(world, corporationId, alliance.Id, now) is { } failure
        )
        {
            throw new InvalidDataException(failure);
        }
        var before = Application(row);
        row.Text = text;
        row.Status = status;
        row.EffectiveAt = status == AllianceApplicationStatus.Accepted ? now + AllianceMembershipRules.AdmissionDelay.Ticks : null;
        Record(world, new(0, alliance.Id, corporationId, ApplicationBefore: before, ApplicationAfter: Application(row)));
        return true;
    }

    private static void ValidateDecision(AllianceApplicationStatus status)
    {
        if (status is not (AllianceApplicationStatus.Accepted or AllianceApplicationStatus.Rejected))
        {
            throw new InvalidDataException("Accept or reject this application.");
        }
    }

    public IReadOnlyList<AllianceApplication> Applications(long actorId, bool forAlliance) =>
        Resident.Economy.Inspect(world =>
        {
            var corporation = CorporationRoleStore.Current(world, actorId);
            CorporationRoleStore.RequireDirector(world, actorId, corporation);
            int allianceId = forAlliance ? Executor(world, actorId).Id : 0;
            var rows = forAlliance
                ? world.Filter<SavedAllianceApplication>(a => a.AllianceId == allianceId)
                : world.Filter<SavedAllianceApplication>(a => a.CorporationId == corporation.Id);
            return (IReadOnlyList<AllianceApplication>)rows.OrderBy(a => a.Id).AsEnumerable().Select(Application).ToArray();
        });

    public void DeclareSupport(long actorId, int corporationId)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        if (corporation.CeoId != actorId)
        {
            throw new InvalidDataException("Only your corporation's CEO can declare executor support.");
        }
        var member =
            world.Set<SavedAllianceMember>().Find(corporation.Id)
            ?? throw new InvalidDataException("Your corporation is not in an alliance.");
        if (world.Set<SavedAllianceMember>().Find(corporationId)?.AllianceId != member.AllianceId)
        {
            throw new InvalidDataException("Support a corporation in your alliance.");
        }
        var before = Member(member);
        if (member.ChosenExecutorId == corporationId)
        {
            return;
        }
        member.ChosenExecutorId = corporationId;
        Record(world, new(0, member.AllianceId, corporation.Id, MemberBefore: before, MemberAfter: Member(member)));
        Recompute(world, member.AllianceId);
        world.Commit();
    }

    public void Remove(long actorId, int corporationId)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        CorporationRoleStore.RequireDirector(world, actorId, corporation);
        var member =
            world.Set<SavedAllianceMember>().Find(corporationId)
            ?? throw new InvalidDataException("That corporation is not in an alliance.");
        if (corporation.Id != corporationId && Executor(world, actorId).Id != member.AllianceId)
        {
            throw new InvalidDataException("Only the executor can expel another member corporation.");
        }
        Depart(world, member, Now);
        world.Commit();
        database.NotifyAccessGroupsChanged();
    }

    public IReadOnlyList<AllianceHistory> History(int corporationId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
            {
                var rows = world
                    .Filter<SavedAllianceHistory>(row => row.CorporationId == corporationId)
                    .OrderByDescending(row => row.StartedAt)
                    .ThenByDescending(row => row.Id)
                    .Select(row => new AllianceHistory(row.AllianceId, row.StartedAt))
                    .ToList();
                if (world.Set<SavedCorporation>().Find(corporationId) is { } corporation)
                {
                    rows.Add(new(null, corporation.CreatedAt));
                }
                return (IReadOnlyList<AllianceHistory>)rows;
            })
        ?? [];

    public IReadOnlyList<AllianceChange> Changes(long actorId, long after) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
            {
                var pilot = world.Pilots.Find(actorId) ?? throw new InvalidDataException("Unknown pilot.");
                var corporation = world.Set<SavedCorporation>().Find(pilot.CorporationId);
                if (corporation is null)
                {
                    return (IReadOnlyList<AllianceChange>)[];
                }
                int? allianceId = world.Set<SavedAllianceMember>().Find(corporation.Id)?.AllianceId;
                var own = world.Filter<SavedAllianceChange>(c => c.CorporationId == corporation.Id && c.Id > after);
                IEnumerable<SavedAllianceChange> shared = allianceId is int id
                    ? world.Filter<SavedAllianceChange>(c => c.AllianceId == id && c.Id > after)
                    : [];
                bool director = CorporationRoleStore.Has(world, actorId, corporation, Eden.Gameplay.Corporations.CorporationRole.Director);
                bool executor =
                    allianceId.HasValue && world.Set<SavedAlliance>().Find(allianceId.Value)?.ExecutorCorporationId == corporation.Id;
                return (IReadOnlyList<AllianceChange>)
                    own.AsEnumerable()
                        .Concat(shared)
                        .OrderBy(c => c.Id)
                        .DistinctBy(c => c.Id)
                        .Select(c => JsonSerializer.Deserialize<AllianceChange>(c.DataJson)!)
                        .Where(c =>
                            c.ApplicationBefore is null && c.ApplicationAfter is null
                            || director && (executor || c.CorporationId == corporation.Id)
                        )
                        .ToArray();
            })
        ?? [];

    internal static SavedAlliance Active(WorldAction world, int id) =>
        world.Set<SavedAlliance>().Find(id) is { ClosedAt: null } row ? row : throw new InvalidDataException("Choose an active alliance.");

    internal static AllianceMember Member(SavedAllianceMember row) =>
        new(row.CorporationId, row.AllianceId, row.JoinedAt, row.ChosenExecutorId);

    internal static AllianceApplication Application(SavedAllianceApplication row) =>
        new(row.Id, row.CorporationId, row.AllianceId, row.Text, row.Status, row.AppliedAt, row.EffectiveAt);

    internal static void Record(WorldAction world, AllianceChange change)
    {
        long id = world.Next("WorldAllianceChange");
        world.Add(
            new SavedAllianceChange
            {
                Id = id,
                AllianceId = change.AllianceId,
                CorporationId = change.CorporationId,
                DataJson = JsonSerializer.Serialize(change with { Id = id }),
            }
        );
    }

    internal static void Recompute(WorldAction world, int allianceId)
    {
        var alliance = world.Set<SavedAlliance>().Find(allianceId)!;
        var members = world.Filter<SavedAllianceMember>(m => m.AllianceId == allianceId).AsEnumerable().Select(Member).ToArray();
        int? executor = AllianceMembershipRules.Executor(members, alliance.ExecutorCorporationId);
        if (executor == alliance.ExecutorCorporationId)
        {
            return;
        }
        var before = Snapshot(world, allianceId)!;
        alliance.ExecutorCorporationId = executor;
        Record(world, new(0, allianceId, 0, Before: before, After: Snapshot(world, allianceId)));
    }

    internal static void Depart(WorldAction world, SavedAllianceMember member, long now)
    {
        var before = Snapshot(world, member.AllianceId)!;
        var previous = Member(member);
        MilitiaOrganizationStore.CorporationDepartedAlliance(world, member.AllianceId, member.CorporationId);
        world.Remove(member);
        ScheduleEmpty(world, member.AllianceId, now);
        foreach (
            var supporter in world
                .Filter<SavedAllianceMember>(m => m.AllianceId == member.AllianceId && m.ChosenExecutorId == member.CorporationId)
                .ToArray()
        )
        {
            var old = Member(supporter);
            supporter.ChosenExecutorId = null;
            Record(world, new(0, member.AllianceId, supporter.CorporationId, MemberBefore: old, MemberAfter: Member(supporter)));
        }
        world.Add(
            new SavedAllianceHistory
            {
                Id = world.Next("WorldAllianceHistory"),
                CorporationId = member.CorporationId,
                StartedAt = now,
            }
        );
        Record(world, new(0, member.AllianceId, member.CorporationId, MemberBefore: previous));
        Recompute(world, member.AllianceId);
        Record(world, new(0, member.AllianceId, member.CorporationId, Before: before, After: Snapshot(world, member.AllianceId)));
    }
}
