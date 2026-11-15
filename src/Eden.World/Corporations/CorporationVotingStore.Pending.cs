using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationVotingStore
{
    public IReadOnlyList<CorporationPendingExpulsion> PendingExpulsions(long actorId)
    {
        Advance();
        return Resident.Economy.Inspect(world =>
        {
            var corporation = CorporationRoleStore.Current(world, actorId);
            CorporationRoleStore.RequireDirector(world, actorId, corporation);
            return (IReadOnlyList<CorporationPendingExpulsion>)
                world
                    .Set<SavedCorporationVote>()
                    .Where(row => row.CorporationId == corporation.Id)
                    .AsEnumerable()
                    .Select(row => row.Snapshot())
                    .Where(vote => vote.Data.PendingExpulsionActorId.HasValue)
                    .Select(vote => new CorporationPendingExpulsion(vote.Data.Draft.Parameter, vote.Data.PendingExpulsionActorId!.Value))
                    .ToArray();
        });
    }

    private void AdvancePendingExpulsions()
    {
        var pending = Volatile.Read(ref _pendingExpulsions);
        if (pending.Length == 0)
        {
            return;
        }
        // Only outstanding subjects are inspected. Waiting in space never scans vote history or opens a write transaction.
        bool ready = Resident.Economy.Inspect(world =>
            pending.Any(id =>
            {
                var vote = world.Set<SavedCorporationVote>().Find(id)?.Snapshot();
                return vote is null
                    || world.Pilots.Find(vote.Data.Draft.Parameter) is not { } pilot
                    || pilot.CorporationId != vote.CorporationId
                    || pilot.ActiveShip.Position is null
                    || CorporationRoleStore.Read(world, pilot.Id, vote.CorporationId).AuthorityHoldVoteId != vote.Id;
            })
        );
        if (!ready)
        {
            return;
        }
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        List<long> remaining = [];
        foreach (long id in Volatile.Read(ref _pendingExpulsions))
        {
            if (world.Set<SavedCorporationVote>().Find(id) is { } row && !CompletePendingExpulsion(world, row, Now))
            {
                remaining.Add(id);
            }
        }
        world.Commit();
        Volatile.Write(ref _pendingExpulsions, remaining.ToArray());
        database.NotifyAccessGroupsChanged();
    }

    private bool CompletePendingExpulsion(WorldAction world, SavedCorporationVote row, long now)
    {
        var vote = row.Snapshot();
        var corporation = world.Set<SavedCorporation>().Find(vote.CorporationId)!;
        var pilot = world.Pilots.Find(vote.Data.Draft.Parameter);
        if (
            pilot?.CorporationId == corporation.Id
            && CorporationRoleStore.Read(world, pilot.Id, corporation.Id).AuthorityHoldVoteId == vote.Id
            && !ExpelMember(world, corporation, vote, now)
        )
        {
            return false;
        }
        row.DataJson = JsonSerializer.Serialize(vote.Data with { PendingExpulsionActorId = null, HoldReleased = true });
        Notify(world, row.Snapshot(), CorporationVoteEvent.Sanctioned);
        return true;
    }
}
