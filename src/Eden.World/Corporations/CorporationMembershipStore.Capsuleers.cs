using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationMembershipStore
{
    internal static bool BotLed(WorldAction world, SavedCorporation corporation) =>
        corporation.CapsuleerFoundingSystemId.HasValue && world.Set<SavedCapsuleerAgent>().Find(corporation.CeoId) is not null;

    private static void OfferToPlayer(
        WorldAction world,
        SavedCorporation corporation,
        Pilot pilot,
        SavedCorporationApplication application,
        long now
    )
    {
        if (
            world is EconomyTransaction
            && world.Settings.Capsuleers.Enabled
            && pilot.AccountId > 0
            && application.Status is CorporationApplicationStatus.Applied or CorporationApplicationStatus.RenegotiatedByApplicant
            && BotLed(world, corporation)
            && corporation.Snapshot(0).Details.ApplicationsEnabled
            && CorporationRoleStore.Has(world, corporation.CeoId, corporation, CorporationRole.PersonnelManager)
            && CanJoin(world, pilot, corporation)
        )
        {
            ChangeApplication(world, corporation.CeoId, pilot, corporation, application, CorporationApplicationStatus.Offered, "", now);
        }
    }

    private static bool CanJoin(WorldAction world, Pilot pilot, SavedCorporation corporation) =>
        pilot.CorporationId != corporation.Id
        && !world.Filter<SavedCorporation>(row => row.CeoId == pilot.Id).Any(row => row.ClosedAt == null)
        && world.Filter<Pilot>(row => row.CorporationId == corporation.Id).Count() < corporation.MemberLimit;

    internal enum BotInvitationReview
    {
        None,
        Deferred,
        Changed,
    }

    private BotInvitationReview SettleBotInvitations(EconomyTransaction world, Pilot pilot, long now) =>
        SettleBotInvitations(world, database, pilot, now);

    internal static BotInvitationReview SettleBotInvitations(EconomyTransaction world, WorldDatabase database, Pilot pilot, long now)
    {
        if (
            !world.Settings.Capsuleers.Enabled
            || pilot.AccountId != CapsuleerCommunityStore.MinerAccountId
            || world.Set<SavedCapsuleerAgent>().Find(pilot.Id) is not { } agent
            || world.Set<SavedCapsuleerSuspension>().Find(pilot.Id) is not null
        )
        {
            return BotInvitationReview.None;
        }
        var pending = world
            .Filter<SavedCorporationApplication>(row => row.PilotId == pilot.Id)
            .Where(row => row.Status == CorporationApplicationStatus.Invited || row.Status == CorporationApplicationStatus.Offered)
            .OrderBy(row => row.Id)
            .ToArray();
        if (pending.Length == 0)
        {
            return BotInvitationReview.None;
        }
        var former = world.Set<SavedCorporation>().Find(pilot.CorporationId);
        bool freeToMove = former is null || BotLed(world, former) && Permission(world, pilot.Id, pilot.Id, now).Allowed;
        var work =
            LiveWorld.Find(database.Path)?.HaulerCheckpoints.GetValueOrDefault(pilot.Id)?.Work
            ?? HaulerCheckpoint.Read(
                world.Filter<SavedCapsuleerHauler>(row => row.PilotId == pilot.Id).Select(row => row.Progress).SingleOrDefault()
            );
        if (
            world.Positions.Find(pilot.ActiveShipId) is not null
            || pilot.ActiveShip.StructureId.HasValue
            || work?.Gang is { Phase: not RoamingGangPhase.Complete }
            || work?.RegionalEscort is not null
            // A loaded hauler can settle at its destination dock before loading the next shipment.
            || work?.Freight is { Stacks.Length: > 0 } freight && freight.Destination != pilot.ActiveShip.StationId
        )
        {
            return BotInvitationReview.Deferred;
        }

        bool changed = false;
        foreach (var invitation in pending)
        {
            if (world.Set<SavedCorporation>().Find(invitation.CorporationId) is not { ClosedAt: null } corporation)
            {
                continue;
            }
            bool local =
                corporation.SystemId == agent.CommunitySystemId
                || database.Catalog.GatesInSystem(agent.CommunitySystemId).Any(gate => gate.DestinationSystemId == corporation.SystemId);
            if (freeToMove && local && CanJoin(world, pilot, corporation))
            {
                ChangeApplication(world, pilot.Id, pilot, corporation, invitation, CorporationApplicationStatus.Joined, "", now);
                world.AfterCommit(database.NotifyAccessGroupsChanged);
                return BotInvitationReview.Changed;
            }
            string reason =
                !freeToMove ? "This pilot is committed to their current corporation."
                : !local ? "This pilot operates outside your recruitment area."
                : "There is no place for this pilot in your corporation.";
            ChangeApplication(
                world,
                pilot.Id,
                pilot,
                corporation,
                invitation,
                CorporationApplicationStatus.RejectedByApplicant,
                reason,
                now
            );
            changed = true;
        }
        return changed ? BotInvitationReview.Changed : BotInvitationReview.None;
    }
}
