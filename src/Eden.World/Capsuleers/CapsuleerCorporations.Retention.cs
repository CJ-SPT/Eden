using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerEmployment(int CorporationId, long JoinedTicks, int HeadquartersId);

internal sealed record CapsuleerProspect(int CorporationId, long FirstConsideredTicks);

internal sealed record CapsuleerWorkContact(int CorporationId, long ExpiresAt);

internal static partial class CapsuleerCorporations
{
    private static CapsuleerOrganization ObserveEmployer(CapsuleerOrganization organization, SavedCorporation? corporation, long ticks) =>
        corporation?.Id == organization.Employment?.CorporationId
            ? organization
            : organization with
            {
                Employment = corporation is null ? null : new(corporation.Id, ticks, corporation.StationId),
                Prospect = null,
            };

    private static bool CanSwitch(
        WorldAction world,
        long pilot,
        SavedCorporation? incumbent,
        CapsuleerProfession profession,
        CapsuleerOrganization organization,
        long activeTicks
    ) =>
        incumbent is not null
        && organization.Employment is { } employment
        && activeTicks - employment.JoinedTicks >= CapsuleerRecruitmentRules.MinimumTenure.Ticks
        && (
            profession != CapsuleerProfession.Combat
            || world.Set<SavedAllianceMember>().Find(incumbent.Id) is null
            || world
                .Filter<Pilot>(member => member.CorporationId == incumbent.Id)
                .AsEnumerable()
                .Any(member => member.Id != pilot && MemberProfession(world, member.Id) == profession)
        );

    private static CapsuleerOrganization ReviewOperatingBase(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        HullClass hull,
        CapsuleerProfession profession,
        CapsuleerOrganization organization,
        SavedCorporation corporation,
        CapsuleerEmployment employment
    )
    {
        if (
            world
                .Filter<Pilot>(member => member.CorporationId == corporation.Id)
                .AsEnumerable()
                .Any(member =>
                    member.Id != pilot.Id
                    && Organization(world, member.Id).Relocation is { } relocation
                    && relocation.HeadquartersId == corporation.StationId
                )
        )
        {
            return organization;
        }
        return ChooseOperatingBase(
            organization with
            {
                Employment = employment with { HeadquartersId = corporation.StationId },
            },
            catalog,
            pilot.ActiveShip.StationId,
            corporation,
            hull,
            profession,
            world.Settings.Capsuleers
        );
    }

    private static CapsuleerOrganization ReviewProspect(
        CatalogDbContext catalog,
        Pilot pilot,
        CapsuleerOrganization organization,
        SavedCorporation incumbent,
        CapsuleerProfession profession,
        Employer? employer,
        long activeTicks
    )
    {
        if (employer is null || !BetterOffer(catalog, pilot, organization, incumbent, profession, employer))
        {
            return organization with { Prospect = null };
        }
        return organization.Prospect?.CorporationId == employer.Corporation.Id
            ? organization
            : organization with
            {
                Prospect = new(employer.Corporation.Id, activeTicks),
            };
    }

    private static bool BetterOffer(
        CatalogDbContext catalog,
        Pilot pilot,
        CapsuleerOrganization organization,
        SavedCorporation incumbent,
        CapsuleerProfession profession,
        Employer employer
    )
    {
        int workplace = organization.OperatingStationId ?? pilot.ActiveShip.StationId;
        var station = catalog.FindStation(workplace);
        bool services =
            station is not null
            && StationCapabilities.Has(
                catalog,
                station.OperationId,
                profession == CapsuleerProfession.Mining ? StationService.Reprocessing : StationService.Market
            );
        int current = CapsuleerRecruitmentRules.OfferScore(
            pilot.Id,
            incumbent.Id,
            0,
            services,
            incumbent.Snapshot(0).Details.TaxRate,
            0,
            0
        );
        return employer.Score - current >= CapsuleerRecruitmentRules.MinimumSwitchImprovement;
    }

    private static bool HasContact(CapsuleerOrganization organization, int corporationId, long activeTicks) =>
        organization.Contacts.Any(contact => contact.CorporationId == corporationId && contact.ExpiresAt > activeTicks);

    internal static void RememberWorkingContact(WorldAction world, long pilotId, int corporationId, long activeTicks)
    {
        if (
            world.Set<SavedCapsuleerAgent>().Find(pilotId) is null
            || world.Pilots.Find(pilotId)?.CorporationId == corporationId
            || world.Set<SavedCorporation>().Find(corporationId) is not { ClosedAt: null }
        )
        {
            return;
        }
        var organization = Organization(world, pilotId);
        CapsuleerWorkContact contact = new(corporationId, checked(activeTicks + CapsuleerRecruitmentRules.ContactLifetime.Ticks));
        var contacts = organization
            .Contacts.Where(existing => existing.ExpiresAt > activeTicks && existing.CorporationId != corporationId)
            .Prepend(contact)
            .Take(CapsuleerRecruitmentRules.MaximumContacts)
            .ToArray();
        SaveOrganization(world, pilotId, organization with { Contacts = contacts });
    }
}
