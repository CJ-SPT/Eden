using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Bookmarks;
using Eden.World.Catalog;
using Eden.World.FactionWarfare;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationLeadershipStore
{
    public void Close(long actorId)
    {
        using WorldActionScope transaction = new(database, TransactionParticipants.None);
        var world = transaction.World;
        var corporation = RequireCeo(world, actorId);
        if (world.Pilots.Find(actorId)!.ActiveShip.Position is not null || Successors(world, corporation).Count != 0)
        {
            throw new InvalidDataException("Dock and transfer CEO to an eligible successor before leaving this corporation.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (
            world is EconomyTransaction
            && world.Set<Eden.Data.World.Alliances.SavedAllianceMember>().Find(corporation.Id) is { } allianceMember
        )
        {
            alliances.Depart(world, allianceMember, now);
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var members = world.Pilots.Where(pilot => pilot.CorporationId == corporation.Id).OrderBy(pilot => pilot.Id).ToArray();
        // Retain the corporation and its asset locations as history. No former member inherits its money or property.
        var before = corporation.Snapshot(members.Length).Details;
        var after = before with { ApplicationsEnabled = false, RecruitmentRevision = checked(before.RecruitmentRevision + 1) };
        string administration = JsonSerializer.Serialize(
            new CorporationAdministrationChange(corporation.Id, before, after, corporation.CeoId, ClosedAt: now)
        );
        foreach (var member in members)
        {
            world.Add(new SavedCorporationChange { PilotId = member.Id, AdministrationJson = administration });
        }
        foreach (
            var application in world
                .Set<SavedCorporationApplication>()
                .Where(row => row.CorporationId == corporation.Id)
                .AsEnumerable()
                .Where(row => !CorporationMembershipRules.Closed(row.Status))
        )
        {
            application.Status = CorporationApplicationStatus.RejectedByCorporation;
            application.UpdatedAt = now;
            CorporationMembershipStore.NotifyApplication(world, application);
        }
        world.RemoveRange(world.Set<SavedCorporationRecruitmentAd>().Where(ad => ad.CorporationId == corporation.Id).ToArray());
        foreach (var office in world.Set<SavedCorporationOffice>().Where(row => row.CorporationId == corporation.Id))
        {
            CorporationStorageStore.Notify(world, new(actorId, office.Snapshot(), true, []));
        }
        foreach (var member in members)
        {
            int destination = CorporationMembershipStore.OriginalCorporation(world, catalog, member.Id);
            CorporationMembershipStore.Move(world, member, destination, now);
        }
        MilitiaOrganizationStore.OrganizationClosed(world, corporation.Id);
        corporation.ClosedAt = now;
        corporation.DetailsJson = JsonSerializer.Serialize(after);
        world.SaveChanges();
        foreach (var member in members)
        {
            PilotBookmarksStore.PruneUnavailableConnections(world, member.Id);
        }
        transaction.Commit();
        database.NotifyAccessGroupsChanged();
    }
}
