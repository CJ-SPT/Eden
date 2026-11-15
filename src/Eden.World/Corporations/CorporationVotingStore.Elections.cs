using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Skills;
using Eden.Gameplay.Corporations;
using Eden.World.Runtime.Economy;

namespace Eden.World.Corporations;

internal sealed partial class CorporationVotingStore
{
    public bool CanRunForCeo(long actorId)
    {
        Advance();
        return Resident.Economy.Inspect(world => CanRunForCeo(world, actorId));
    }

    private static bool CanRunForCeo(WorldAction world, long actorId)
    {
        var pilot = world.Pilots.Find(actorId);
        var corporation = pilot is null ? null : world.Set<SavedCorporation>().Find(pilot.CorporationId);
        if (corporation is not { ClosedAt: null } || corporation.CeoId == actorId || !EligibleSuccessor(world, actorId, corporation.Id))
        {
            return false;
        }
        long shares = world.Set<SavedCorporationShareHolding>().Find(corporation.Id, actorId)?.Shares ?? 0;
        long issued = world.Set<SavedCorporationShareIssue>().Find(corporation.Id)?.Shares ?? 0;
        return CorporationVotingRules.HasCeoElectionShares(shares, issued)
            && !CorporationRoleStore.Read(world, corporation.CeoId, corporation.Id).AuthorityHoldVoteId.HasValue
            && !world
                .Set<SavedCorporationVote>()
                .Where(row => row.CorporationId == corporation.Id)
                .AsEnumerable()
                .Any(row => row.Snapshot().Data is { Election: not null, HoldReleased: false });
    }

    private static bool EligibleSuccessor(WorldAction world, long pilotId, int corporationId) =>
        world.Pilots.Find(pilotId)?.CorporationId == corporationId
        && world.Set<PilotSkill>().Find(pilotId, CorporationRules.ManagementSkill) is not null
        && CorporationRoleStore.Read(world, pilotId, corporationId) is { BlockRoles: false, AuthorityHoldVoteId: null };

    private static long MembershipId(WorldAction world, long pilotId) =>
        world
            .Set<SavedCorporationEmployment>()
            .Where(row => row.PilotId == pilotId)
            .OrderByDescending(row => row.Id)
            .Select(row => row.Id)
            .FirstOrDefault();

    private static long AdvanceElection(WorldAction world, SavedCorporationVote row, long now)
    {
        var vote = row.Snapshot();
        var election = vote.Data.Election!;
        if (!vote.Data.Settled && now < vote.EndsAt)
        {
            return vote.EndsAt;
        }
        if (!vote.Data.Settled)
        {
            var corporation = world.Set<SavedCorporation>().Find(vote.CorporationId)!;
            bool elected =
                corporation.ClosedAt is null
                && corporation.CeoId == election.IncumbentId
                && CorporationRoleStore.Read(world, election.IncumbentId, corporation.Id).AuthorityHoldVoteId == vote.Id
                && CorporationVotingRules.Winner(vote.Data) == 0
                && EligibleSuccessor(world, vote.Data.CreatorId, vote.CorporationId)
                && MembershipId(world, vote.Data.CreatorId) == election.CandidateMembershipId;
            if (elected)
            {
                CorporationLeadershipStore.ChangeCeo(world, corporation, vote.Data.CreatorId, vote.EndsAt, vote.Id);
                election = election with { AuthorityAvailableAt = checked(vote.EndsAt + CorporationVotingRules.CeoAuthorityDelay.Ticks) };
            }
            else
            {
                ReleaseAuthority(world, vote, election.IncumbentId);
            }
            row.DataJson = JsonSerializer.Serialize(
                vote.Data with
                {
                    Settled = true,
                    AppliedAt = elected ? vote.EndsAt : null,
                    HoldReleased = !elected,
                    Election = election,
                }
            );
            Notify(world, row.Snapshot(), CorporationVoteEvent.Closed);
            vote = row.Snapshot();
        }
        if (vote.Data.HoldReleased || election.AuthorityAvailableAt is not long release)
        {
            return long.MaxValue;
        }
        if (now < release)
        {
            return release;
        }
        ReleaseAuthority(world, vote, vote.Data.CreatorId);
        row.DataJson = JsonSerializer.Serialize(vote.Data with { HoldReleased = true });
        Notify(world, row.Snapshot(), CorporationVoteEvent.Sanctioned);
        return long.MaxValue;
    }
}
