using Eden.Gameplay.Corporations;
using Eden.World.Bookmarks;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationMembershipStore
{
    public CorporationMemberRemovalResult RemoveMany(long actorId, IReadOnlyList<long> pilotIds)
    {
        CorporationMembershipRules.CheckRemovalTargets(actorId, pilotIds);
        var resident =
            LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Bulk member removal requires an active resident world.");
        using var world = resident.Economy.Begin(TransactionParticipants.None);
        var corporation = CorporationRoleStore.Current(world, actorId);
        CorporationRoleStore.RequireDirector(world, actorId, corporation);
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<long> kicked = [];
        List<long> notKicked = [];
        long now = Now;
        foreach (long pilotId in pilotIds.Distinct())
        {
            var pilot = world.Pilots.Find(pilotId);
            if (pilot is null || pilot.CorporationId != corporation.Id || !Permission(world, actorId, pilotId).Allowed)
            {
                notKicked.Add(pilotId);
                continue;
            }
            Move(world, pilot, OriginalCorporation(world, catalog, pilotId), now);
            world.SaveChanges();
            PilotBookmarksStore.PruneUnavailableConnections(world, pilotId);
            kicked.Add(pilotId);
        }
        world.Commit();
        if (kicked.Count > 0)
        {
            database.NotifyAccessGroupsChanged();
        }
        return new(kicked, notKicked);
    }
}
