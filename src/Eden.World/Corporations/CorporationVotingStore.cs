using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable(InjectionType.Singleton)]
internal sealed partial class CorporationVotingStore(WorldDatabase database, TimeProvider clock) : ICorporationVoting
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Corporation voting requires an active resident world.");
    private long _nextDeadline = long.MinValue;
    private long[] _pendingExpulsions = [];
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public bool CanView(long actorId, int corporationId) => Resident.Economy.Inspect(world => CanView(world, actorId, corporationId));

    private static bool CanView(WorldAction world, long actorId, int corporationId)
    {
        var corporation = world.Set<SavedCorporation>().Find(corporationId);
        return corporation is { ClosedAt: null }
            && (
                corporation.CeoId == actorId
                || CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.Director)
                || Owners(world, actorId).Any(owner => world.Set<SavedCorporationShareHolding>().Find(corporationId, owner)?.Shares > 0)
            );
    }

    private static long[] Owners(WorldAction world, long actorId) =>
        world
            .Set<SavedCorporation>()
            .Where(row => row.CeoId == actorId && row.ClosedAt == null)
            .AsEnumerable()
            .Where(row => CorporationRoleStore.Has(world, actorId, row, CorporationRole.Director))
            .Select(row => (long)row.Id)
            .Prepend(actorId)
            .ToArray();

    private static void RequireView(WorldAction world, long actorId, int corporationId)
    {
        if (!CanView(world, actorId, corporationId))
        {
            throw new InvalidDataException("Corporation leadership or shares are required to view these votes.");
        }
    }

    public CorporationVoteListing List(long actorId, int corporationId)
    {
        Advance();
        return Resident.Economy.Inspect(world =>
        {
            RequireView(world, actorId, corporationId);
            var votes = world
                .Set<SavedCorporationVote>()
                .Where(row => row.CorporationId == corporationId)
                .OrderByDescending(row => row.Id)
                .AsEnumerable()
                .Select(row => row.Snapshot())
                .ToArray();
            return new CorporationVoteListing(
                votes.Where(vote => vote.EndsAt > Now).ToArray(),
                votes.Where(vote => vote.EndsAt <= Now).Take(CorporationVotingRules.ClosedPageSize).ToArray()
            );
        });
    }

    public CorporationVoteCase Read(long actorId, int corporationId, long caseId)
    {
        Advance();
        return Resident.Economy.Inspect(world =>
        {
            RequireView(world, actorId, corporationId);
            return Case(world, corporationId, caseId).Snapshot();
        });
    }

    public long Create(long actorId, CorporationVoteDraft draft)
    {
        CorporationVotingRules.Validate(draft);
        Advance();
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        CorporationCeoElection? election = null;
        if (draft.Type == CorporationVoteType.Ceo)
        {
            if (draft.Parameter != actorId || draft.Parameter1 != 0 || draft.Parameter2 != 0 || !CanRunForCeo(world, actorId))
            {
                throw new InvalidDataException("Only an eligible member may propose themselves for CEO.");
            }
            election = new(corporation.CeoId, MembershipId(world, actorId));
        }
        else
        {
            CorporationRoleStore.RequireDirector(world, actorId, corporation);
        }
        long now = Now;
        if (
            world.Set<SavedCorporationVote>().Count(row => row.CorporationId == corporation.Id && row.EndsAt > now)
            >= CorporationVotingRules.MaximumOpen
        )
        {
            throw new InvalidDataException("Close existing corporation votes before starting another.");
        }
        var rights = world
            .Set<SavedCorporationShareHolding>()
            .Where(row => row.CorporationId == corporation.Id && row.Shares > 0)
            .Select(row => new CorporationVoteRight(row.ShareholderId, row.Shares))
            .ToArray();
        var row = world
            .Add(
                new SavedCorporationVote
                {
                    Id = world.Next("WorldCorporationVote"),
                    CorporationId = corporation.Id,
                    StartedAt = now,
                    EndsAt = checked(now + draft.Days * TimeSpan.TicksPerDay),
                    DataJson = JsonSerializer.Serialize(new CorporationVoteData(actorId, draft, rights, [], Election: election)),
                }
            )
            .Entity;
        world.SaveChanges();
        if (draft.Type == CorporationVoteType.Expel)
        {
            HoldMember(world, corporation, draft.Parameter, row.Id, now);
        }
        else if (election is not null)
        {
            HoldAuthority(world, corporation, election.IncumbentId, row.Id, now);
        }
        var blueprintChange = draft.Type is CorporationVoteType.LockBlueprint or CorporationVoteType.UnlockBlueprint
            ? PrepareBlueprintVote(world, corporation, row)
            : null;
        Notify(world, row.Snapshot(), CorporationVoteEvent.Created, blueprintChange);
        world.Commit();
        Interlocked.Exchange(ref _nextDeadline, Math.Min(Volatile.Read(ref _nextDeadline), row.EndsAt));
        return row.Id;
    }

    public void Cast(long actorId, int corporationId, long caseId, int optionId)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        RequireView(world, actorId, corporationId);
        var row = Case(world, corporationId, caseId);
        var vote = row.Snapshot();
        var owners = Owners(world, actorId);
        var rights = vote.Data.Rights.Where(right => owners.Contains(right.OwnerId)).ToArray();
        if (
            Now >= vote.EndsAt
            || optionId < 0
            || optionId >= vote.Data.Draft.Options.Count
            || rights.Length == 0
            || vote.Data.Ballots.Any(ballot => ballot.PilotId == actorId || owners.Contains(ballot.OwnerId))
        )
        {
            throw new InvalidDataException("Choose an open vote and an unused voting right with a valid option.");
        }
        var ballots = vote
            .Data.Ballots.Concat(rights.Select(right => new CorporationBallot(right.OwnerId, actorId, optionId, right.Weight, Now)))
            .ToArray();
        row.DataJson = JsonSerializer.Serialize(vote.Data with { Ballots = ballots });
        Notify(world, row.Snapshot(), CorporationVoteEvent.Cast);
        world.Commit();
    }

    public IReadOnlyList<CorporationVoteCase> Actions(long actorId)
    {
        Advance();
        return Resident.Economy.Inspect(world =>
        {
            var corporation = CorporationRoleStore.Current(world, actorId);
            CorporationRoleStore.RequireDirector(world, actorId, corporation);
            return (IReadOnlyList<CorporationVoteCase>)
                world
                    .Set<SavedCorporationVote>()
                    .Where(row => row.CorporationId == corporation.Id)
                    .AsEnumerable()
                    .Select(row => row.Snapshot())
                    .Where(vote =>
                        vote.Data.Settled
                        && vote.Data.Draft.Type
                            is CorporationVoteType.Shares
                                or CorporationVoteType.Expel
                                or CorporationVoteType.UnlockBlueprint
                        && CorporationVotingRules.Winner(vote.Data) == 0
                    )
                    .ToArray();
        });
    }

    public void Activate(long actorId, long caseId)
    {
        Advance();
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        var row = Case(world, corporation.Id, caseId);
        var vote = row.Snapshot();
        if (
            corporation.CeoId != actorId
            || !CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.Director)
            || !vote.Data.Settled
            || vote.Data.AppliedAt.HasValue
            || Now >= CorporationVotingRules.Expires(vote)
            || CorporationVotingRules.Winner(vote.Data) != 0
            || vote.Data.Draft.Type is not (CorporationVoteType.Shares or CorporationVoteType.Expel or CorporationVoteType.UnlockBlueprint)
        )
        {
            throw new InvalidDataException("Only the CEO may enact an unused, successful and unexpired sanction.");
        }
        bool completed = true;
        CorporationBlueprintLockChange? blueprintChange = null;
        if (vote.Data.Draft.Type == CorporationVoteType.Expel)
        {
            completed = ExpelMember(world, corporation, vote, Now);
        }
        else if (vote.Data.Draft.Type == CorporationVoteType.UnlockBlueprint)
        {
            blueprintChange = UnlockBlueprint(world, corporation, vote);
        }
        else
        {
            IssueShares(world, corporation.Id, vote.Data.Draft.Parameter);
        }
        row.DataJson = JsonSerializer.Serialize(
            vote.Data with
            {
                AppliedAt = Now,
                HoldReleased = completed,
                PendingExpulsionActorId = completed ? null : actorId,
            }
        );
        Notify(world, row.Snapshot(), CorporationVoteEvent.Sanctioned, blueprintChange);
        world.Commit();
        if (!completed)
        {
            Volatile.Write(ref _pendingExpulsions, [.. Volatile.Read(ref _pendingExpulsions), vote.Id]);
        }
        if (vote.Data.Draft.Type == CorporationVoteType.Expel)
        {
            database.NotifyAccessGroupsChanged();
        }
    }

    private static void IssueShares(WorldAction world, int corporationId, long quantity)
    {
        var issue = world.Set<SavedCorporationShareIssue>().Find(corporationId)!;
        var treasury = world.Set<SavedCorporationShareHolding>().Find(corporationId, (long)corporationId)!;
        if (quantity > long.MaxValue - issue.Shares || quantity > long.MaxValue - treasury.Shares)
        {
            throw new InvalidDataException("The share issuance exceeds the supported supply.");
        }
        long before = treasury.Shares;
        issue.Shares += quantity;
        treasury.Shares += quantity;
        CorporationShareStore.Notify(world, new(corporationId, corporationId, before, treasury.Shares));
    }

    public void Advance()
    {
        AdvancePendingExpulsions();
        long now = Now;
        if (now < Volatile.Read(ref _nextDeadline))
        {
            return;
        }
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        long next = long.MaxValue;
        List<long> pending = [];
        bool departed = false;
        foreach (var row in world.Set<SavedCorporationVote>())
        {
            var vote = row.Snapshot();
            if (vote.Data.Election is not null)
            {
                next = Math.Min(next, AdvanceElection(world, row, now));
                continue;
            }
            if (vote.Data.Draft.Type == CorporationVoteType.LockBlueprint)
            {
                next = Math.Min(next, AdvanceBlueprintVote(world, row, now));
                continue;
            }
            if (vote.Data.PendingExpulsionActorId.HasValue)
            {
                if (CompletePendingExpulsion(world, row, now))
                {
                    departed = true;
                }
                else
                {
                    pending.Add(vote.Id);
                }
                continue;
            }
            if (vote.Data.Draft.Type == CorporationVoteType.Expel && !vote.Data.HoldReleased)
            {
                long release = CorporationVotingRules.Winner(vote.Data) == 0 ? CorporationVotingRules.Expires(vote) : vote.EndsAt;
                if (now >= release)
                {
                    ReleaseMember(world, vote);
                    row.DataJson = JsonSerializer.Serialize(vote.Data with { HoldReleased = true });
                    vote = row.Snapshot();
                }
                else
                {
                    next = Math.Min(next, release);
                }
            }
            if (vote.Data.Settled)
            {
                continue;
            }
            if (vote.EndsAt > now)
            {
                next = Math.Min(next, vote.EndsAt);
                continue;
            }
            row.DataJson = JsonSerializer.Serialize(vote.Data with { Settled = true });
            Notify(world, row.Snapshot(), CorporationVoteEvent.Closed);
        }
        world.Commit();
        Interlocked.Exchange(ref _nextDeadline, next);
        Volatile.Write(ref _pendingExpulsions, pending.ToArray());
        if (departed)
        {
            database.NotifyAccessGroupsChanged();
        }
    }

    private static SavedCorporationVote Case(WorldAction world, int corporationId, long caseId) =>
        world.Set<SavedCorporationVote>().Find(caseId) is { } row && row.CorporationId == corporationId
            ? row
            : throw new InvalidDataException("Unknown corporation vote.");

    private static void Notify(
        WorldAction world,
        CorporationVoteCase vote,
        CorporationVoteEvent kind,
        CorporationBlueprintLockChange? blueprintChange = null
    )
    {
        var corporation = world.Set<SavedCorporation>().Find(vote.CorporationId)!;
        var before = corporation.Snapshot(0).Details;
        var after = before with { GovernanceRevision = checked(before.GovernanceRevision + 1) };
        corporation.DetailsJson = JsonSerializer.Serialize(after);
        var holders = vote.Data.Rights.Select(right => right.OwnerId).ToArray();
        var companyVoters = world
            .Set<SavedCorporation>()
            .Where(row => Enumerable.Contains(holders, (long)row.Id) && row.ClosedAt == null)
            .Select(row => row.CeoId)
            .ToArray();
        var recipients = world
            .Pilots.Where(pilot =>
                pilot.CorporationId == corporation.Id
                || Enumerable.Contains(holders, pilot.Id)
                || Enumerable.Contains(companyVoters, pilot.Id)
            )
            .Select(pilot => pilot.Id)
            .ToArray();
        string json = JsonSerializer.Serialize(
            new CorporationAdministrationChange(corporation.Id, before, after, Vote: new(vote.Id, kind), BlueprintLock: blueprintChange)
        );
        foreach (long recipient in recipients)
        {
            world.Add(new SavedCorporationChange { PilotId = recipient, AdministrationJson = json });
        }
    }
}
