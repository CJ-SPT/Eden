using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Bookmarks;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationMembershipStore
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Corporation member removal requires an active resident world.");

    public CorporationMemberRemovalResult RemoveMany(long actorId, IReadOnlyList<long> pilotIds)
    {
        CorporationMembershipRules.CheckRemovalTargets(actorId, pilotIds);
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        CorporationRoleStore.RequireDirector(world, actorId, corporation);
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<long> kicked = [];
        List<long> notKicked = [];
        long now = Now;
        bool departed = false;
        foreach (long pilotId in pilotIds.Distinct())
        {
            var pilot = world.Pilots.Find(pilotId);
            if (pilot is null || pilot.CorporationId != corporation.Id || !Permission(world, actorId, pilotId, now).Allowed)
            {
                notKicked.Add(pilotId);
                continue;
            }
            departed |= RequestRemoval(world, catalog, actorId, pilot, now);
            kicked.Add(pilotId);
        }
        world.Commit();
        if (departed)
        {
            database.NotifyAccessGroupsChanged();
        }
        return new(kicked, notKicked);
    }

    private static bool RequestRemoval(WorldAction world, CatalogDbContext catalog, long actorId, Pilot pilot, long now)
    {
        int destination = OriginalCorporation(world, catalog, pilot.Id);
        var existing = world.Set<SavedCorporationRemoval>().Find(pilot.Id);
        if (actorId != pilot.Id && pilot.ActiveShip.Position is not null)
        {
            long membership = MembershipId(world, pilot.Id);
            if (existing is null)
            {
                existing = world.Add(new SavedCorporationRemoval { PilotId = pilot.Id }).Entity;
            }
            else if (
                existing.CorporationId == pilot.CorporationId
                && existing.MembershipId == membership
                && CorporationRemovalStore.Valid(world, existing, now)
            )
            {
                return false;
            }
            existing.CorporationId = pilot.CorporationId;
            existing.ActorId = actorId;
            existing.MembershipId = membership;
            existing.DestinationCorporationId = destination;
            existing.RequestedAt = now;
            return false;
        }
        Depart(world, pilot, destination, now);
        return true;
    }

    internal static long MembershipId(WorldAction world, long pilotId) =>
        world
            .Set<SavedCorporationEmployment>()
            .Where(row => row.PilotId == pilotId)
            .OrderByDescending(row => row.Id)
            .Select(row => row.Id)
            .FirstOrDefault();

    internal static void Depart(WorldAction world, Pilot pilot, int destination, long now)
    {
        if (world.Set<SavedCorporationRemoval>().Find(pilot.Id) is { } pending)
        {
            world.Remove(pending);
        }
        Move(world, pilot, destination, now);
        world.SaveChanges();
        PilotBookmarksStore.PruneUnavailableConnections(world, pilot.Id);
    }
}
