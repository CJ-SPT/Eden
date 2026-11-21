using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.FactionWarfare;
using Eden.World.Runtime.Economy;

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
            && world.Settings.Capsuleers.AutomaticRecruitment
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

    internal static bool CanJoin(WorldAction world, Pilot pilot, SavedCorporation corporation) =>
        pilot.CorporationId != corporation.Id
        && !world.Filter<SavedCorporation>(row => row.CeoId == pilot.Id).Any(row => row.ClosedAt == null)
        && world.Filter<Pilot>(row => row.CorporationId == corporation.Id).Count() < corporation.MemberLimit
        && MilitiaOrganizationStore.CorporateAdmissionFailure(world, pilot, corporation.Id) is null;
}
