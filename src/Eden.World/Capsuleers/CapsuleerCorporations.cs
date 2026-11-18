using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal static class CapsuleerCorporations
{
    internal static bool Place(EconomyTransaction world, CatalogDbContext catalog, WorldDatabase database, long pilotId, long now)
    {
        if (
            !world.Settings.Capsuleers.Enabled
            || world.Set<SavedCapsuleerAgent>().Find(pilotId) is not { } agent
            || !Eligible(world, catalog, pilotId)
            || world.Pilots.Find(pilotId) is not { } pilot
            || world.Positions.Find(pilot.ActiveShipId) is not null
            || pilot.ActiveShip.StructureId.HasValue
        )
        {
            return false;
        }

        var homes = catalog
            .Definitions.GatesInSystem(agent.CommunitySystemId)
            .Select(gate => gate.DestinationSystemId)
            .Prepend(agent.CommunitySystemId)
            .Distinct()
            .ToArray();
        var corporations = homes
            .SelectMany(home => world.Filter<SavedCorporation>(row => row.SystemId == home).AsEnumerable())
            .Where(row =>
                row.CapsuleerFoundingSystemId.HasValue
                && row.ClosedAt is null
                && world.Set<SavedCapsuleerAgent>().Find(row.CeoId) is not null
            )
            .OrderByDescending(row => row.SystemId == agent.CommunitySystemId)
            .ThenBy(row => row.Id);
        var employer = corporations.FirstOrDefault(row =>
            world.Filter<Pilot>(member => member.CorporationId == row.Id).Count()
            < Math.Min(row.MemberLimit, CapsuleerCorporationRules.TargetMembers)
        );
        if (employer is not null)
        {
            CorporationMembershipStore.Enroll(world, pilot, employer, now);
            world.AfterCommit(database.NotifyAccessGroupsChanged);
            return true;
        }

        return Found(world, catalog, database, pilot, homes, now);
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
        bool initial = !world.Set<SavedCorporation>().Any(row => row.CapsuleerFoundingSystemId == system);
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
        const int maximumNameAttempts = 64;
        int attempt = 0;
        do
        {
            if (attempt == maximumNameAttempts)
            {
                return false;
            }
            details = CapsuleerCorporationRules.Identity(systemName, seed, system, attempt++, mining, frontier);
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
