using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
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

internal sealed record CapsuleerOrganization(long DueTicks = 0, long? ApplicationId = null, CapsuleerProfession? Profession = null);

internal static partial class CapsuleerCorporations
{
    private sealed record Employer(SavedCorporation Corporation, CapsuleerTrafficRoute Route, int Score);

    private static readonly CatalogDefinition<int[]> s_recruitmentSystems = new("capsuleer-recruitment-systems", RecruitmentSystems);

    internal static CapsuleerOrganization Organization(WorldAction world, long pilot) =>
        world.Set<SavedCapsuleerOrganization>().Find(pilot) is { } row
            ? JsonSerializer.Deserialize<CapsuleerOrganization>(row.StateJson)!
            : new();

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
        if (organization.DueTicks > activeTicks)
        {
            return false;
        }
        var hull = (HullClass)(catalog.Definitions.Items.GetValueOrDefault(pilot.ActiveShip.TypeId)?.GroupId ?? 0);
        var profession =
            agent.Career == CapsuleerCareer.Mining
                ? CapsuleerProfession.Mining
                : CapsuleerCareerTraining.Profession(catalog, pilot.ActiveShip.TypeId, hull) ?? CapsuleerProfession.Hauling;
        long nextReview = checked(activeTicks + CapsuleerRecruitmentRules.ReviewDelay(pilotId).Ticks);
        organization = organization with { Profession = profession, DueTicks = nextReview };
        var former = world.Set<SavedCorporation>().Find(pilot.CorporationId);
        if (former?.CeoId == pilotId && CanRecruit(world, former))
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
        organization = organization with { ApplicationId = null };
        if (!Eligible(world, catalog, pilotId))
        {
            SaveOrganization(world, pilotId, organization);
            return false;
        }
        var employer = FindEmployer(world, catalog, pilot, hull, profession, station.SolarSystemId, now);
        if (employer is not null)
        {
            var submitted = CorporationMembershipStore.SubmitApplication(
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
                    DueTicks = checked(activeTicks + CapsuleerRecruitmentRules.OfferDelay(pilotId).Ticks),
                }
            );
            return false;
        }
        SaveOrganization(world, pilotId, organization);
        var homeArea = CapsuleerTrafficRoutes.Neighbours(catalog, agent.CommunitySystemId).Prepend(agent.CommunitySystemId).ToArray();
        return !world.Settings.FreshUniverse || agent.Career == CapsuleerCareer.Mining
            ? Found(world, catalog, database, pilot, homeArea, now)
            : false;
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
        if (world.Set<SavedCorporation>().Find(application.CorporationId) is not { ClosedAt: null } corporation)
        {
            SaveOrganization(world, pilot.Id, organization with { ApplicationId = null });
            return false;
        }
        bool autonomous = organization.ApplicationId == application.Id;
        var employer = Evaluate(world, catalog, pilot, hull, profession, corporation, autonomous);
        bool suitable = free && employer is not null && (!autonomous || CanRecruit(world, corporation));
        bool offered = application.Status is CorporationApplicationStatus.Invited or CorporationApplicationStatus.Offered;
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
            SaveOrganization(world, pilot.Id, organization with { ApplicationId = null });
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
        SaveOrganization(world, pilot.Id, organization with { ApplicationId = null });
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
        long now
    )
    {
        Employer? best = null;
        int region = catalog.RequireSystem(system).RegionId;
        foreach (int home in catalog.Definition(s_recruitmentSystems, system))
        {
            foreach (var corporation in world.Filter<SavedCorporation>(row => row.SystemId == home))
            {
                if (!CanRecruit(world, corporation))
                {
                    continue;
                }
                bool local = home == system || CapsuleerTrafficRoutes.Neighbours(catalog, system).Contains(home);
                bool advertised =
                    !local
                    && catalog.RequireSystem(home).RegionId == region
                    && world.Filter<SavedCorporationRecruitmentAd>(ad => ad.CorporationId == corporation.Id).Any(ad => ad.ExpiresAt > now);
                if (
                    (local || advertised)
                    && Evaluate(world, catalog, pilot, hull, profession, corporation, true) is { } option
                    && (best is null || option.Score > best.Score || option.Score == best.Score && corporation.Id < best.Corporation.Id)
                )
                {
                    best = option;
                }
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
        bool autonomous
    )
    {
        if (!CorporationMembershipStore.CanJoin(world, pilot, corporation) || catalog.FindStation(corporation.StationId) is not { } station)
        {
            return null;
        }
        int vacancies = CapsuleerRecruitmentRules.DesiredMembers(profession);
        if (autonomous)
        {
            var members = world.Filter<Pilot>(member => member.CorporationId == corporation.Id).Select(member => member.Id).ToArray();
            vacancies -= members.Count(member => MemberProfession(world, member) == profession);
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
            maximumJumps: CapsuleerRecruitmentRules.MaximumJumps
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
        return !autonomous || score >= CapsuleerRecruitmentRules.MinimumOfferScore ? new(corporation, route, score) : null;
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
