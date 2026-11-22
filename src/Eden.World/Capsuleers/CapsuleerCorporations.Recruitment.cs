using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerOrganization(long DueTicks = 0, long? ApplicationId = null, CapsuleerProfession? Profession = null)
{
    public CapsuleerCareer? ActiveWorker { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? HuntReserveJobId { get; init; }
    public long? StarterMarketJobId { get; init; }
    public int? OperatingStationId { get; init; }
    public CapsuleerRelocation? Relocation { get; init; }
    public CapsuleerEmployment? Employment { get; init; }
    public CapsuleerProspect? Prospect { get; init; }
    public long? ApplicationExpiresAt { get; init; }
    public CapsuleerWorkContact[] Contacts { get; init; } = [];
    public int RelocationsCompleted { get; init; }
}

internal static partial class CapsuleerCorporations
{
    private sealed record Employer(SavedCorporation Corporation, int Score, bool Referred);

    private static readonly CatalogDefinition<int[]> s_recruitmentSystems = new("capsuleer-recruitment-systems", RecruitmentSystems);

    internal static CapsuleerOrganization Organization(WorldAction world, long pilot) =>
        ReadOrganization(world.Set<SavedCapsuleerOrganization>().Find(pilot));

    internal static CapsuleerOrganization ReadOrganization(SavedCapsuleerOrganization? row) =>
        row is null ? new() : JsonSerializer.Deserialize<CapsuleerOrganization>(row.StateJson)!;

    internal static void SaveOrganization(WorldAction world, long pilot, CapsuleerOrganization organization)
    {
        var row =
            world.Set<SavedCapsuleerOrganization>().Find(pilot) ?? world.Add(new SavedCapsuleerOrganization { PilotId = pilot }).Entity;
        row.StateJson = JsonSerializer.Serialize(organization);
    }

    internal static bool Place(
        EconomyTransaction world,
        CatalogDbContext catalog,
        WorldDatabase database,
        long pilotId,
        long now,
        long activeTicks = 0
    )
    {
        if (
            !world.Settings.Capsuleers.Enabled
            || world.Pilots.Find(pilotId) is not { AccountId: CapsuleerCommunityStore.MinerAccountId } pilot
            || world.Set<SavedCapsuleerAgent>().Find(pilotId) is not { } agent
            || world.Set<SavedCapsuleerSuspension>().Find(pilotId) is not null
            || world.Positions.Find(pilot.ActiveShipId) is not null
            || pilot.ActiveShip.StructureId.HasValue
            || catalog.FindStation(pilot.ActiveShip.StationId) is not { } station
        )
        {
            return false;
        }
        var organization = Organization(world, pilotId);
        if (organization.DueTicks > activeTicks || organization.Relocation is not null)
        {
            return false;
        }
        var hull = (HullClass)(catalog.Definitions.Items.GetValueOrDefault(pilot.ActiveShip.TypeId)?.GroupId ?? 0);
        var profession =
            CapsuleerCareerTraining.Profession(catalog, pilot.ActiveShip.TypeId, hull)
            ?? (
                (organization.ActiveWorker ?? agent.Career) == CapsuleerCareer.Mining ? CapsuleerProfession.Mining
                : world.Settings.FreshUniverse
                    ? LiveWorld.Find(database.Path)?.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work.CareerPlan?.Profession
                        ?? FreshCareerRules.Profession(agent.Slot)
                : CapsuleerProfession.Hauling
            );
        long nextReview = checked(activeTicks + CapsuleerRecruitmentRules.ReviewDelay(pilotId).Ticks);
        organization = organization with { Profession = profession, DueTicks = nextReview };
        var former = world.Set<SavedCorporation>().Find(pilot.CorporationId);
        organization = ObserveEmployer(organization, former, activeTicks);
        if (world.Settings.FreshUniverse && former?.CeoId == pilotId && CorporationMembershipStore.BotLed(world, former))
        {
            FundFreshCharter(world, former, now);
        }
        bool recruiting = world.Settings.Capsuleers.AutomaticRecruitment;
        if (recruiting && former?.CeoId == pilotId && CanRecruit(world, former))
        {
            Advertise(world, former, now);
        }
        if (Committed(world, database, pilot))
        {
            return false;
        }
        var applications = world
            .Filter<SavedCorporationApplication>(row => row.PilotId == pilotId)
            .Where(row => !CorporationMembershipRules.Closed(row.Status))
            .OrderBy(row => row.Id)
            .ToArray();
        var application =
            applications.FirstOrDefault(row => row.Status is CorporationApplicationStatus.Invited or CorporationApplicationStatus.Offered)
            ?? applications.FirstOrDefault(row => row.Id == organization.ApplicationId);
        bool free =
            former is null
            || CorporationMembershipStore.BotLed(world, former)
                && CorporationMembershipStore.Permission(world, pilotId, pilotId, now).Allowed;
        if (application is not null)
        {
            return ReviewApplication(world, catalog, database, pilot, hull, profession, organization, application, free, now, activeTicks);
        }
        organization = organization with { ApplicationId = null, ApplicationExpiresAt = null };
        if (former is not null && organization.Employment is { } employment && former.StationId != employment.HeadquartersId)
        {
            SaveOrganization(
                world,
                pilotId,
                ReviewOperatingBase(world, catalog, pilot, hull, profession, organization, former, employment)
            );
            return false;
        }
        if (!recruiting)
        {
            SaveOrganization(world, pilotId, organization);
            return false;
        }
        bool independent = Eligible(world, catalog, pilotId);
        bool canSwitch = free && CanSwitch(world, pilotId, former, profession, organization, activeTicks);
        if (!independent && !canSwitch)
        {
            SaveOrganization(world, pilotId, organization);
            return false;
        }
        var employer = FindEmployer(world, catalog, pilot, hull, profession, station.SolarSystemId, now, organization, activeTicks);
        if (canSwitch && former is not null)
        {
            organization = ReviewProspect(catalog, pilot, organization, former, profession, employer, activeTicks);
            if (
                organization.Prospect is not { } prospect
                || activeTicks - prospect.FirstConsideredTicks < CapsuleerRecruitmentRules.SustainedInterest.Ticks
            )
            {
                SaveOrganization(world, pilotId, organization);
                return false;
            }
        }
        if (employer is not null)
        {
            var submitted = employer.Referred
                ? CorporationMembershipStore.SubmitInvitation(world, pilot, employer.Corporation, now)
                : CorporationMembershipStore.SubmitApplication(
                    world,
                    pilot,
                    employer.Corporation,
                    $"I am looking for regular {profession.ToString().ToLowerInvariant()} work.",
                    now
                );
            SaveOrganization(
                world,
                pilotId,
                organization with
                {
                    ApplicationId = submitted.Id,
                    ApplicationExpiresAt = checked(activeTicks + CapsuleerRecruitmentRules.ApplicationLifetime.Ticks),
                    DueTicks = checked(activeTicks + CapsuleerRecruitmentRules.OfferDelay(pilotId).Ticks),
                }
            );
            return false;
        }
        SaveOrganization(world, pilotId, organization);
        var homeArea = CapsuleerTrafficRoutes.Neighbours(catalog, agent.CommunitySystemId).Prepend(agent.CommunitySystemId).ToArray();
        return independent && (!world.Settings.FreshUniverse || agent.Career == CapsuleerCareer.Mining)
            ? Found(world, catalog, database, pilot, homeArea, now)
            : false;
    }

    private static void FundFreshCharter(EconomyTransaction world, SavedCorporation corporation, long now)
    {
        const long workingCapital = CorporationRules.CreationFeeCents;
        const int key = CorporationWalletRules.MasterKey;
        long balance = world.Set<PilotWallet>().Find(corporation.CeoId)?.Cents ?? 0;
        long contribution = Math.Min(balance - workingCapital, workingCapital - corporation.BalanceCents);
        if (contribution > 0)
        {
            CorporationWalletStore.Transfer(
                world,
                corporation.CeoId,
                new(false, key, corporation.Id, key, contribution, "Fresh charter operating capital"),
                now
            );
        }
    }

    private static bool ReviewApplication(
        EconomyTransaction world,
        CatalogDbContext catalog,
        WorldDatabase database,
        Pilot pilot,
        HullClass hull,
        CapsuleerProfession profession,
        CapsuleerOrganization organization,
        SavedCorporationApplication application,
        bool free,
        long now,
        long activeTicks
    )
    {
        bool autonomous = organization.ApplicationId == application.Id;
        var retry = autonomous ? organization with { ApplicationId = null, ApplicationExpiresAt = null, Prospect = null } : organization;
        if (world.Set<SavedCorporation>().Find(application.CorporationId) is not { ClosedAt: null } corporation)
        {
            SaveOrganization(world, pilot.Id, retry);
            return false;
        }
        bool referred = HasContact(organization, corporation.Id, activeTicks);
        var employer = Evaluate(world, catalog, pilot, hull, profession, corporation, autonomous, referred);
        bool unexpired = organization.ApplicationExpiresAt.GetValueOrDefault(long.MaxValue) > activeTicks;
        bool offered = application.Status is CorporationApplicationStatus.Invited or CorporationApplicationStatus.Offered;
        bool suitable =
            free
            && employer is not null
            && (!autonomous || CanRecruit(world, corporation) && unexpired)
            && (
                offered
                || application.Status is CorporationApplicationStatus.Applied or CorporationApplicationStatus.RenegotiatedByApplicant
            );
        if (
            suitable
            && autonomous
            && organization.Prospect is not null
            && world.Set<SavedCorporation>().Find(pilot.CorporationId) is { } incumbent
        )
        {
            suitable =
                CanSwitch(world, pilot.Id, incumbent, profession, organization, activeTicks)
                && BetterOffer(catalog, pilot, organization, incumbent, profession, employer!);
        }
        if (!suitable)
        {
            var closed = offered ? CorporationApplicationStatus.RejectedByApplicant : CorporationApplicationStatus.Withdrawn;
            CorporationMembershipStore.ChangeApplication(
                world,
                pilot.Id,
                pilot,
                corporation,
                application,
                closed,
                "I cannot commit to the opening or its operating area.",
                now
            );
            SaveOrganization(world, pilot.Id, retry);
            return false;
        }
        if (!offered)
        {
            CorporationMembershipStore.ChangeApplication(
                world,
                corporation.CeoId,
                pilot,
                corporation,
                application,
                CorporationApplicationStatus.Offered,
                "We have an opening for your work.",
                now
            );
            SaveOrganization(
                world,
                pilot.Id,
                organization with
                {
                    DueTicks = checked(activeTicks + CapsuleerRecruitmentRules.OfferDelay(pilot.Id).Ticks),
                }
            );
            return false;
        }
        CorporationMembershipStore.ChangeApplication(
            world,
            pilot.Id,
            pilot,
            corporation,
            application,
            CorporationApplicationStatus.Joined,
            "",
            now
        );
        var appointed = retry with
        {
            ApplicationId = null,
            ApplicationExpiresAt = null,
            Prospect = null,
            Employment = new(corporation.Id, activeTicks, corporation.StationId),
        };
        SaveOrganization(
            world,
            pilot.Id,
            ChooseOperatingBase(appointed, catalog, pilot.ActiveShip.StationId, corporation, hull, profession, world.Settings.Capsuleers)
        );
        world.AfterCommit(database.NotifyAccessGroupsChanged);
        return true;
    }

    private static Employer? FindEmployer(
        EconomyTransaction world,
        CatalogDbContext catalog,
        Pilot pilot,
        HullClass hull,
        CapsuleerProfession profession,
        int system,
        long now,
        CapsuleerOrganization organization,
        long activeTicks
    )
    {
        Employer? best = null;
        int region = catalog.RequireSystem(system).RegionId;
        var neighbours = CapsuleerTrafficRoutes.Neighbours(catalog, system);
        var homes = catalog.Definition(s_recruitmentSystems, system).ToHashSet();
        foreach (var contact in organization.Contacts)
        {
            if (contact.ExpiresAt > activeTicks && world.Set<SavedCorporation>().Find(contact.CorporationId) is { } employer)
            {
                homes.Add(employer.SystemId);
            }
        }
        foreach (var corporation in world.Filter<SavedCorporation>(row => homes.Contains(row.SystemId)))
        {
            if (!CanRecruit(world, corporation))
            {
                continue;
            }
            int home = corporation.SystemId;
            bool local = home == system || neighbours.Contains(home);
            bool referred = HasContact(organization, corporation.Id, activeTicks);
            bool advertised =
                !local
                && catalog.RequireSystem(home).RegionId == region
                && world.Filter<SavedCorporationRecruitmentAd>(ad => ad.CorporationId == corporation.Id).Any(ad => ad.ExpiresAt > now);
            if (
                (local || advertised || referred)
                && Evaluate(world, catalog, pilot, hull, profession, corporation, true, referred) is { } option
                && (best is null || option.Score > best.Score || option.Score == best.Score && corporation.Id < best.Corporation.Id)
            )
            {
                best = option;
            }
        }
        return best;
    }

    private static Employer? Evaluate(
        EconomyTransaction world,
        CatalogDbContext catalog,
        Pilot pilot,
        HullClass hull,
        CapsuleerProfession profession,
        SavedCorporation corporation,
        bool autonomous,
        bool referred
    )
    {
        if (!CorporationMembershipStore.CanJoin(world, pilot, corporation) || catalog.FindStation(corporation.StationId) is not { } station)
        {
            return null;
        }
        int vacancies = CapsuleerRecruitmentRules.DesiredMembers(profession);
        if (autonomous)
        {
            vacancies -= world.CorporationMembers(corporation.Id).Count(member => MemberProfession(world, member.PilotId) == profession);
            if (vacancies <= 0)
            {
                return null;
            }
        }
        var route = CapsuleerTrafficRoutes.ToStation(
            catalog,
            pilot.ActiveShip.StationId,
            station.Id,
            hull,
            maximumJumps: CapsuleerRecruitmentRules.MaximumJumps,
            policy: world.Settings.Capsuleers
        );
        if (route is null)
        {
            return null;
        }
        bool services = StationCapabilities.Has(
            catalog,
            station.OperationId,
            profession == CapsuleerProfession.Mining ? StationService.Reprocessing : StationService.Market
        );
        int danger = route.Systems.Skip(1).Count(system => catalog.Definitions.SystemSecurity.GetValueOrDefault(system) < 0.45);
        int score = CapsuleerRecruitmentRules.OfferScore(
            pilot.Id,
            corporation.Id,
            vacancies,
            services,
            corporation.Snapshot(0).Details.TaxRate,
            route.Systems.Length - 1,
            danger
        );
        score += referred ? CapsuleerRecruitmentRules.ReferralValue : 0;
        return !autonomous || score >= CapsuleerRecruitmentRules.MinimumOfferScore ? new(corporation, score, referred) : null;
    }

    private static int[] RecruitmentSystems(CatalogDbContext catalog, int origin)
    {
        Queue<(int System, int Distance)> pending = new();
        pending.Enqueue((origin, 0));
        HashSet<int> seen = [origin];
        List<int> result = [];
        while (result.Count < CapsuleerRecruitmentRules.MaximumSearchSystems && pending.TryDequeue(out var next))
        {
            result.Add(next.System);
            if (next.Distance < CapsuleerRecruitmentRules.MaximumJumps)
            {
                foreach (int neighbor in CapsuleerTrafficRoutes.Neighbours(catalog, next.System))
                {
                    if (seen.Add(neighbor))
                    {
                        pending.Enqueue((neighbor, next.Distance + 1));
                    }
                }
            }
        }
        return result.ToArray();
    }

    private static CapsuleerProfession? MemberProfession(WorldAction world, long pilot) =>
        Organization(world, pilot).Profession
        ?? (
            world.Set<SavedCapsuleerAgent>().Find(pilot)?.Career switch
            {
                CapsuleerCareer.Mining => CapsuleerProfession.Mining,
                CapsuleerCareer.Hauling => CapsuleerProfession.Hauling,
                _ => null,
            }
        );

    private static bool CanRecruit(WorldAction world, SavedCorporation corporation) =>
        corporation.ClosedAt is null
        && CorporationMembershipStore.BotLed(world, corporation)
        && corporation.Snapshot(0).Details.ApplicationsEnabled
        && CorporationRoleStore.Has(world, corporation.CeoId, corporation, CorporationRole.PersonnelManager);

    private static void Advertise(EconomyTransaction world, SavedCorporation corporation, long now)
    {
        const int days = CapsuleerRecruitmentRules.AdDays;
        long fee = CorporationRecruitmentRules.Fee(days, true);
        if (
            corporation.BalanceCents < fee
            || world.Filter<Pilot>(pilot => pilot.CorporationId == corporation.Id).Count()
                >= Math.Min(corporation.MemberLimit, CapsuleerCorporationRules.TargetMembers)
            || world.Filter<SavedCorporationRecruitmentAd>(ad => ad.CorporationId == corporation.Id).Any(ad => ad.ExpiresAt > now)
        )
        {
            return;
        }
        CorporationRecruitmentDraft draft = new(
            "Independent pilots welcome",
            "Apply for work based at our headquarters.",
            0,
            0,
            0,
            0,
            0,
            [corporation.CeoId]
        );
        CorporationRecruitmentRules.Validate(draft);
        CorporationRecruitmentStore.CreateAdvertisement(world, corporation, draft, days, fee, now);
    }

    internal static bool Committed(WorldAction world, WorldDatabase database, Pilot pilot)
    {
        var live = LiveWorld.Find(database.Path);
        var work =
            live?.HaulerCheckpoints.GetValueOrDefault(pilot.Id)?.Work
            ?? HaulerCheckpoint.Read(world.Filter<SavedCapsuleerHauler>(row => row.PilotId == pilot.Id).SingleOrDefault()?.Progress);
        var mining =
            live?.MinerCheckpoints.GetValueOrDefault(pilot.Id)?.Work
            ?? MinerCheckpoint.Read(world.Filter<SavedCapsuleerCommunity>(row => row.PilotId == pilot.Id).SingleOrDefault()?.Progress);
        return work?.Gang is { Phase: not RoamingGangPhase.Complete }
            || work?.RegionalEscort is not null
            || work?.CorporationSupply is not null
            || work?.SovereigntyExpedition is not null
            || work?.HomeDefenseAllianceId is not null
            || work?.Freight is { Stacks.Length: > 0 } freight && freight.Destination != pilot.ActiveShip.StationId
            || mining?.FactoryStage is not null
            || world
                .Filter<IndustryJob>(job => job.PilotId == pilot.Id)
                .Any(job => job.OutputCorporationId != null && job.Status == IndustryJobStatus.Installed);
    }
}
