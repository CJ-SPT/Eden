using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal static partial class CapsuleerCorporations
{
    internal static bool CanFlyTogether(LiveWorld live, long pilot, long other) =>
        CanFlyTogether(live, live.Economy.CorporationAffiliation(pilot), other);

    internal static bool CanFlyTogether(LiveWorld live, DockedPilot pilot, long other) =>
        CanFlyTogether(live, (pilot.CorpId, pilot.Identity.AllianceId), other);

    private static bool CanFlyTogether(LiveWorld live, (int Corporation, int? Alliance) left, long other)
    {
        var right = live.Economy.CorporationAffiliation(other);
        return PatrolRules.Unaffiliated(left.Corporation, right.Corporation)
            || PatrolRules.CorporateAllies(left.Corporation, left.Alliance, right.Corporation, right.Alliance);
    }

    private static bool Found(
        EconomyTransaction world,
        CatalogDbContext catalog,
        WorldDatabase database,
        Pilot pilot,
        int[] homes,
        long now
    )
    {
        long pilotId = pilot.Id;
        var station = catalog.RequireStation(pilot.ActiveShip.StationId);
        int system = station.SolarSystemId;
        if (!homes.Contains(system))
        {
            return false;
        }
        // Count actual eligible local pilots, including ones still flying. Each joins at its own safe dock.
        var area = catalog.Definitions.GatesInSystem(system).Select(gate => gate.DestinationSystemId).Prepend(system).Distinct();
        var roster = area.SelectMany(home => world.Filter<SavedCapsuleerAgent>(row => row.CommunitySystemId == home).AsEnumerable())
            .Where(row => Eligible(world, catalog, row.PilotId))
            .Take(CapsuleerCorporationRules.FoundingRoster)
            .ToArray();
        if (roster.Length < CapsuleerCorporationRules.FoundingRoster)
        {
            return false;
        }

        var wallet = world.Set<PilotWallet>().Find(pilotId);
        bool initial = !world.Settings.FreshUniverse && !world.Set<SavedCorporation>().Any(row => row.CapsuleerFoundingSystemId == system);
        double rank = catalog.FittingAttributes(CorporationRules.ManagementSkill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant);
        if (wallet is null || rank <= 0 || !initial && wallet.Cents < CorporationRules.CreationFeeCents)
        {
            return false;
        }
        var skill = world.Set<PilotSkill>().Find(pilotId, CorporationRules.ManagementSkill);
        long points = TrainedSkill.PointsForLevel(rank, 1);
        if (!initial && (skill?.Points ?? 0) < points)
        {
            return false;
        }
        if (initial && wallet.Cents > long.MaxValue - CorporationRules.CreationFeeCents)
        {
            return false;
        }
        long seed = world.Set<SavedCapsuleerProvisioning>().Find(1)?.WorldSeed ?? 0;
        bool mining = roster.Any(row => row.Career == CapsuleerCareer.Mining);
        bool frontier = catalog.Definitions.SystemSecurity.GetValueOrDefault(system, 1) <= 0;
        string systemName = catalog.RequireSystem(system).Name;
        CorporationDetails details;
        int attempt = 0;
        do
        {
            if (attempt == CapsuleerCorporationRules.MaximumIdentityAttempts)
            {
                return false;
            }
            details = CapsuleerCorporationRules.Identity(systemName, seed, pilotId, pilot.Name, attempt++, mining, frontier);
        } while (PlayerCorporationStore.IdentityTaken(world, catalog, details));

        if (initial)
        {
            CapsuleerCommunityStore.GrantRequirements(world, catalog, pilotId, CorporationRules.ManagementSkill, []);
            skill ??= world.Add(new PilotSkill { PilotId = pilotId, TypeId = CorporationRules.ManagementSkill }).Entity;
            skill.Points = Math.Max(skill.Points, points);
            wallet.Cents = checked(wallet.Cents + CorporationRules.CreationFeeCents);
            CorporationWalletStore.AddEntry(
                world,
                pilotId,
                CorporationWalletRules.MasterKey,
                now,
                station.OwnerId,
                CorporationRules.CreationFeeCents,
                wallet.Cents,
                WalletEntryType.GmCashTransfer,
                system,
                "Capsuleer corporation founding allowance"
            );
        }
        var created = PlayerCorporationStore.Create(world, catalog, pilotId, details, now);
        world.Set<SavedCorporation>().Find(created.Corporation.Id)!.CapsuleerFoundingSystemId = system;
        world.AfterCommit(database.NotifyAccessGroupsChanged);
        return true;
    }

    private static bool Eligible(EconomyTransaction world, CatalogDbContext catalog, long id) =>
        world.Pilots.Find(id)
            is { AccountId: CapsuleerCommunityStore.MinerAccountId, CorporationId: < CorporationRules.FirstCorporationId } pilot
        && catalog.Read(CatalogData.Client.Schools, (long)pilot.SchoolId)?.CorporationID == pilot.CorporationId
        && world.Set<SavedCapsuleerSuspension>().Find(id) is null
        && !world
            .Filter<SavedCorporationEmployment>(row => row.PilotId == id)
            .Any(row => row.CorporationId >= CorporationRules.FirstCorporationId);
}
