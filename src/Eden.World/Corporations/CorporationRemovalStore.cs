using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

[Injectable(InjectionType.Singleton)]
internal sealed class CorporationRemovalStore(WorldDatabase database, TimeProvider clock, ICorporationVoting voting)
    : ICorporationMemberRemovals
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Corporation removal queues require an active resident world.");

    public IReadOnlyList<CorporationPendingExpulsion> Pending(long actorId)
    {
        voting.Advance();
        Advance(validateWaitingRequests: true);
        return Resident.Economy.Inspect(world =>
        {
            var corporation = CorporationRoleStore.Current(world, actorId);
            CorporationRoleStore.RequireDirector(world, actorId, corporation);
            var sanctioned = CorporationVotingStore.PendingExpulsions(world, actorId);
            return (IReadOnlyList<CorporationPendingExpulsion>)
                world
                    .Set<SavedCorporationRemoval>()
                    .Where(row => row.CorporationId == corporation.Id)
                    .AsEnumerable()
                    .Select(row => new CorporationPendingExpulsion(row.PilotId, row.ActorId))
                    .Concat(sanctioned)
                    .DistinctBy(row => row.PilotId)
                    .OrderBy(row => row.PilotId)
                    .ToArray();
        });
    }

    public void Advance() => Advance(validateWaitingRequests: false);

    private void Advance(bool validateWaitingRequests)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        // Idle maintenance only looks up queued subjects' membership and docking state, without role/share/history scans.
        if (
            !Resident.Economy.Inspect(world =>
                world.Set<SavedCorporationRemoval>().AsEnumerable().Any(row => Ready(world, row, now, validateWaitingRequests))
            )
        )
        {
            return;
        }
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        bool departed = false;
        foreach (
            var row in world
                .Set<SavedCorporationRemoval>()
                .AsEnumerable()
                .Where(row => Ready(world, row, now, validateWaitingRequests))
                .ToArray()
        )
        {
            if (!Valid(world, row, now))
            {
                world.Remove(row);
            }
            else if (world.Pilots.Find(row.PilotId) is { } pilot && pilot.ActiveShip.Position is null)
            {
                CorporationMembershipStore.Depart(world, pilot, row.DestinationCorporationId, now);
                departed = true;
            }
        }
        world.Commit();
        if (departed)
        {
            database.NotifyAccessGroupsChanged();
        }
    }

    private static bool Ready(WorldAction world, SavedCorporationRemoval row, long now, bool validateWaitingRequests)
    {
        var pilot = world.Pilots.Find(row.PilotId);
        return pilot is null
            || pilot.CorporationId != row.CorporationId
            || pilot.ActiveShip.Position is null
            || validateWaitingRequests && !Valid(world, row, now);
    }

    internal static bool Valid(WorldAction world, SavedCorporationRemoval row, long now) =>
        world.Pilots.Find(row.PilotId)?.CorporationId == row.CorporationId
        && world.Pilots.Find(row.ActorId) is not null
        && world.Set<SavedCorporation>().Find(row.CorporationId) is { ClosedAt: null }
        && CorporationMembershipStore.MembershipId(world, row.PilotId) == row.MembershipId
        && CorporationMembershipStore.Permission(world, row.ActorId, row.PilotId, now).Allowed;
}
