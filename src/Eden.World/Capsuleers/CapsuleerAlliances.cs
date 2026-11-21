using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Alliances;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.FactionWarfare;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed partial class CapsuleerAlliances(
    WorldDatabase database,
    TimeProvider clock,
    IHumanPresence presence,
    AllianceStore alliances,
    PilotMarketStore market,
    MarketChangeSignal marketSignal
) : ICapsuleerAlliancePlanning
{
    private const int SystemsPerReview = 32;
    private const int MaximumIdentityAttempts = 32;
    private static readonly TimeSpan s_reviewInterval = TimeSpan.FromSeconds(15);
    private static readonly CatalogDefinition<int[]> s_stationSystems = new(
        "capsuleer-alliance-headquarters",
        (catalog, _) => catalog.Definitions.Stations.Values.Select(station => station.SolarSystemId).Distinct().Order().ToArray()
    );
    private readonly Lock _gate = new();
    private long? _lastReview;
    private int? _nextSystem;

    public void Advance()
    {
        if (!database.Settings.Capsuleers.Enabled || LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        var activity = presence.Read();
        if (activity.ActiveSystems.Count == 0)
        {
            return;
        }
        lock (_gate)
        {
            long timestamp = clock.GetTimestamp();
            if (_lastReview is long last && clock.GetElapsedTime(last, timestamp) < s_reviewInterval)
            {
                return;
            }

            _lastReview = timestamp;
            using CatalogDbContext catalog = new(database.CatalogPath);
            int[] systems = catalog.Definitions.GlobalDefinition(s_stationSystems);
            if (systems.Length == 0)
            {
                return;
            }
            int start = _nextSystem ?? Array.BinarySearch(systems, activity.ActiveSystems[0]);
            start = start >= 0 ? start : ~start % systems.Length;
            int examined = Math.Min(systems.Length, SystemsPerReview);
            using var world = live.Economy.Begin(TransactionParticipants.None);
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            for (int offset = 0; offset < examined; offset++)
            {
                int index = (start + offset) % systems.Length;
                // A failing local review rolls back and returns to the normal rotation.
                _nextSystem = (index + 1) % systems.Length;
                if (ReviewSystem(world, catalog, systems[index], now))
                {
                    world.Commit();
                    break;
                }
            }
        }
    }

    private bool ReviewSystem(EconomyTransaction world, CatalogDbContext catalog, int system, long now)
    {
        var local = world
            .Filter<SavedCorporation>(row => row.SystemId == system)
            .AsEnumerable()
            .Where(row => Viable(world, row))
            .OrderBy(row => row.Id)
            .ToArray();
        if (local.Length == 0)
        {
            return false;
        }
        var independent = local.Where(row => IsIndependent(world, row, now)).ToArray();
        var neighbors =
            independent.Length == 0
                ? []
                : CapsuleerTrafficRoutes
                    .Neighbours(catalog, system)
                    .Prepend(system)
                    .SelectMany(home => world.Filter<SavedCorporation>(row => row.SystemId == home).AsEnumerable())
                    .Where(row => Viable(world, row))
                    .OrderByDescending(row => row.SystemId == system)
                    .ThenBy(row => row.Id)
                    .ToArray();
        foreach (var corporation in independent)
        {
            var existingExecutor = neighbors.FirstOrDefault(row => CanRecruit(world, row, corporation.Id, now));
            if (existingExecutor is not null)
            {
                int allianceId = world.Set<SavedAllianceMember>().Find(existingExecutor.Id)!.AllianceId;
                ApplyAndAccept(world, corporation, existingExecutor, allianceId, now);
                return true;
            }

            var partner = neighbors.FirstOrDefault(row => row.Id != corporation.Id && IsIndependent(world, row, now));
            if (partner is not null && TryFound(world, catalog, corporation, partner, now))
            {
                return true;
            }
        }
        return local.Any(corporation =>
            TryMaintainAllianceDues(world, corporation) || TryProcureFirstHub(world, catalog, corporation, now)
        );
    }

    private static bool Viable(WorldAction world, SavedCorporation corporation) =>
        corporation.ClosedAt is null
        && CorporationMembershipStore.BotLed(world, corporation)
        && world.Set<SavedCapsuleerSuspension>().Find(corporation.CeoId) is null
        && CorporationRoleStore.Has(world, corporation.CeoId, corporation, CorporationRole.Director)
        && world
            .Filter<Pilot>(pilot => pilot.CorporationId == corporation.Id)
            .AsEnumerable()
            .Where(pilot => ActiveBot(world, pilot.Id))
            .Take(CapsuleerCorporationRules.FoundingRoster)
            .Count() == CapsuleerCorporationRules.FoundingRoster;

    private static bool ActiveBot(WorldAction world, long pilot) =>
        world.Set<SavedCapsuleerAgent>().Find(pilot) is not null && world.Set<SavedCapsuleerSuspension>().Find(pilot) is null;

    private static bool IsIndependent(WorldAction world, SavedCorporation corporation, long now) =>
        world.Set<SavedAllianceMember>().Find(corporation.Id) is null
        && MilitiaOrganizationStore.AllianceAdmissionFailure(world, corporation.Id, null, now) is null
        && !world
            .Filter<SavedAllianceApplication>(row => row.CorporationId == corporation.Id)
            .Any(row => row.Status == AllianceApplicationStatus.New || row.Status == AllianceApplicationStatus.Accepted);

    private static bool CanRecruit(WorldAction world, SavedCorporation executor, int applicant, long now)
    {
        if (
            world.Set<SavedAllianceMember>().Find(executor.Id) is not { } member
            || world.Set<SavedAlliance>().Find(member.AllianceId) is not { ClosedAt: null } alliance
            || alliance.ExecutorCorporationId != executor.Id
            || world.Set<SavedCapsuleerAgent>().Find(alliance.CreatorId) is null
            || world.Filter<SavedAllianceHistory>(row => row.CorporationId == applicant).Any(row => row.AllianceId == alliance.Id)
        )
        {
            return false;
        }

        var applications = world.Filter<SavedAllianceApplication>(row => row.AllianceId == alliance.Id).ToArray();
        int reserved =
            world.Filter<SavedAllianceMember>(row => row.AllianceId == alliance.Id).Count()
            + applications.Count(row => row.Status == AllianceApplicationStatus.Accepted);
        return reserved < CapsuleerAllianceRules.TargetCorporations
            && !applications.Any(row => row.CorporationId == applicant)
            && CanFund(world, executor, reserved + 1, 0)
            && MilitiaOrganizationStore.AllianceAdmissionFailure(world, applicant, alliance.Id, now) is null;
    }

    private bool TryFound(EconomyTransaction world, CatalogDbContext catalog, SavedCorporation founder, SavedCorporation partner, long now)
    {
        long ceo = founder.CeoId;
        var wallet = world.Set<PilotWallet>().Find(ceo);
        long shortfall = Math.Max(0, AllianceRules.CreationFeeCents - (wallet?.Cents ?? 0));
        if (!CanFund(world, founder, CapsuleerAllianceRules.FoundingCorporations, shortfall))
        {
            return false;
        }
        double rank = catalog.FittingAttributes(AllianceRules.EmpireControlSkill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
        if (rank <= 0 || !double.IsFinite(rank) || catalog.Definitions.Systems.GetValueOrDefault(founder.SystemId) is not { } system)
        {
            return false;
        }
        long seed = world.Set<SavedCapsuleerProvisioning>().Find(1)?.WorldSeed ?? 0;
        for (int attempt = 0; attempt < MaximumIdentityAttempts; attempt++)
        {
            var details = CapsuleerAllianceRules.Identity(system.Name, seed, system.Id, attempt);
            if (AllianceStore.IdentityTaken(world, catalog, details))
            {
                continue;
            }

            if (shortfall > 0)
            {
                const int key = CorporationWalletRules.MasterKey;
                CorporationWalletStore.Transfer(world, ceo, new(true, key, ceo, key, shortfall, "Alliance registration"), now);
            }
            CapsuleerCommunityStore.GrantRequirements(world, catalog, ceo, AllianceRules.EmpireControlSkill, []);
            var skill =
                world.Set<PilotSkill>().Find(ceo, AllianceRules.EmpireControlSkill)
                ?? world.Add(new PilotSkill { PilotId = ceo, TypeId = AllianceRules.EmpireControlSkill }).Entity;
            skill.Points = Math.Max(skill.Points, TrainedSkill.PointsForLevel(rank, AllianceRules.RequiredEmpireControlLevel));
            var created = alliances.Create(world, catalog, ceo, details, now).Alliance;
            CorporationBillingStore.DefaultAllianceMaintenance(world, ceo);
            ApplyAndAccept(world, partner, founder, created.Id, now);
            return true;
        }
        return false;
    }

    internal static bool CanFund(WorldAction world, SavedCorporation corporation, int count, long registration)
    {
        const int master = CorporationWalletRules.MasterKey;
        int division = CorporationBillingStore.Saved(world, corporation.Id)?.DivisionKey ?? master;
        return corporation.BalanceCents >= registration
            && CorporationWalletStore.ReadBalance(world, corporation, division) - (division == master ? registration : 0)
                >= AllianceBillingRules.Amount(count);
    }

    private static void ApplyAndAccept(WorldAction world, SavedCorporation applicant, SavedCorporation executor, int allianceId, long now)
    {
        const string terms = "Cooperation between neighboring capsuleer corporations.";
        AllianceStore.Apply(world, applicant.CeoId, allianceId, terms, now);
        AllianceStore.Decide(world, executor.CeoId, applicant.Id, terms, AllianceApplicationStatus.Accepted, now);
    }
}
