using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Corporations;

internal sealed partial class CorporationVotingStore
{
    private static void HoldMember(WorldAction world, SavedCorporation corporation, long pilotId, long voteId, long now)
    {
        if (pilotId == corporation.CeoId || world.Pilots.Find(pilotId)?.CorporationId != corporation.Id)
        {
            throw new InvalidDataException("Choose an ordinary member of this corporation for expulsion.");
        }
        var access = CorporationRoleStore.Read(world, pilotId, corporation.Id);
        if (access.AuthorityHoldVoteId.HasValue)
        {
            throw new InvalidDataException("A vote already suspends this member's roles.");
        }
        var before = CorporationRoleStore.Effective(world, pilotId, corporation);
        CorporationRoleStore.Save(
            world,
            pilotId,
            corporation.Id,
            access with
            {
                AuthorityHoldVoteId = voteId,
                RolesRemovedAt = before.Roles.HasAuthority ? now : access.RolesRemovedAt,
            }
        );
        var after = CorporationRoleStore.Effective(world, pilotId, corporation);
        CorporationRoleStore.Notify(world, corporation.Id, new(corporation.Id, pilotId, before, after), null);
    }

    private static void ReleaseMember(WorldAction world, CorporationVoteCase vote)
    {
        long pilotId = vote.Data.Draft.Parameter;
        var corporation = world.Set<SavedCorporation>().Find(vote.CorporationId)!;
        var access = CorporationRoleStore.Read(world, pilotId, corporation.Id);
        // A departure/rejoin or closure must never resurrect the old membership's authority.
        if (world.Pilots.Find(pilotId)?.CorporationId != corporation.Id || access.AuthorityHoldVoteId != vote.Id)
        {
            return;
        }
        var before = CorporationRoleStore.Effective(world, pilotId, corporation);
        CorporationRoleStore.Save(world, pilotId, corporation.Id, access with { AuthorityHoldVoteId = null });
        var after = CorporationRoleStore.Effective(world, pilotId, corporation);
        CorporationRoleStore.Notify(world, corporation.Id, new(corporation.Id, pilotId, before, after), null);
    }

    private bool ExpelMember(WorldAction world, SavedCorporation corporation, CorporationVoteCase vote, long now)
    {
        var pilot = world.Pilots.Find(vote.Data.Draft.Parameter);
        if (
            pilot is null
            || pilot.CorporationId != corporation.Id
            || pilot.Id == corporation.CeoId
            || CorporationRoleStore.Read(world, pilot.Id, corporation.Id).AuthorityHoldVoteId != vote.Id
        )
        {
            throw new InvalidDataException("The voted member no longer belongs to this corporation or has become its CEO.");
        }
        if (pilot.ActiveShip.Position is not null)
        {
            return false;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        CorporationMembershipStore.Move(world, pilot, CorporationMembershipStore.OriginalCorporation(world, catalog, pilot.Id), now);
        world.SaveChanges();
        Eden.World.Bookmarks.PilotBookmarksStore.PruneUnavailableConnections(world, pilot.Id);
        return true;
    }
}
