using System.Security.Cryptography;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable]
internal sealed class CapsuleerProvisioner(WorldDatabase database, TimeProvider clock) : ICapsuleerProvisioning
{
    public CapsuleerProvisioningTurn Reconcile(int maximumCommunities = int.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCommunities);
        cancellationToken.ThrowIfCancellationRequested();
        if (LiveWorld.Find(database.Path) is not null)
        {
            throw new InvalidOperationException("Population provisioning requires a stopped world.");
        }
        lock (database.MotionLock)
        {
            return Provision(maximumCommunities, cancellationToken);
        }
    }

    private CapsuleerProvisioningTurn Provision(int limit, CancellationToken cancellationToken)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        long seed;
        Dictionary<int, int> homes;
        using (WorldDbContext world = new(database.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            var state = world.Set<SavedCapsuleerProvisioning>().SingleOrDefault();
            if (state is null)
            {
                state = new() { Id = 1, WorldSeed = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) & long.MaxValue };
                world.Add(state);
                world.SaveChanges();
            }
            seed = state.WorldSeed;
            homes = world.Set<SavedCapsuleerPopulation>().ToDictionary(c => c.SystemId, c => c.HomeStationId);
            transaction.Commit();
        }
        var origins = CapsuleerProvisioningCatalog.Origins(catalog);
        var sites = CapsuleerProvisioningCatalog.Plan(catalog, database.Settings.Capsuleers.GlobalProvisioning, homes, origins.Length != 0);
        using (WorldDbContext world = new(database.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            var records = world.Set<SavedCapsuleerSystem>().ToDictionary(s => s.SystemId);
            foreach (var site in sites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!records.TryGetValue(site.SystemId, out var record))
                {
                    record = new() { SystemId = site.SystemId };
                    world.Add(record);
                }
                record.Eligibility = site.Eligibility;
                record.HomeStationId = site.HomeStationId;
                record.DestinationStationId = site.DestinationStationId;
                record.CatalogFingerprint = catalog.Definitions.Identity.SourceFingerprint;
                record.PolicyVersion = CapsuleerProvisioningRules.Version;
            }
            world.SaveChanges();
            transaction.Commit();
        }
        HashSet<int> complete;
        using (WorldDbContext world = new(database.Path))
        {
            complete = world
                .Set<SavedCapsuleerAgent>()
                .GroupBy(a => a.CommunitySystemId)
                .Where(g => g.Count() == CapsuleerProvisioningRules.FoundersPerCommunity)
                .Select(g => g.Key)
                .ToHashSet();
        }
        int created = 0;
        int pending = 0;
        foreach (var site in sites.Where(s => s.Eligibility == CapsuleerEligibility.Eligible))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (complete.Contains(site.SystemId))
            {
                continue;
            }
            using WorldDbContext world = new(database.Path);
            using var transaction = world.Database.BeginTransaction();
            var agents = world.Set<SavedCapsuleerAgent>().Where(a => a.CommunitySystemId == site.SystemId).ToArray();
            if (agents.Any(a => a.Career == CapsuleerCareer.Mining) && agents.Any(a => a.Career == CapsuleerCareer.Hauling))
            {
                continue;
            }
            if (created == limit)
            {
                pending++;
                continue;
            }
            // Materialize and validate the real deposits before granting this pair.
            // Existing saved fields are read as-is; Populate never replenishes them.
            AsteroidFieldStore.Populate(world, catalog, site.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            world.SaveChanges();
            var available = world
                .Set<WorldAsteroid>()
                .Where(a => a.SystemId == site.SystemId && a.Quantity > 0)
                .Select(a => a.TypeId)
                .Distinct()
                .ToArray();
            if (!available.Any(type => CapsuleerProvisioningCatalog.SupportedOre(catalog, type)))
            {
                world.Set<SavedCapsuleerSystem>().Single(s => s.SystemId == site.SystemId).Eligibility = CapsuleerEligibility.NoUsableOre;
                world.SaveChanges();
                transaction.Commit();
                continue;
            }
            EnsureAccount(world);
            var population = world.Set<SavedCapsuleerPopulation>().Find(site.SystemId);
            if (population is null)
            {
                world.Add(
                    new SavedCapsuleerPopulation
                    {
                        SystemId = site.SystemId,
                        HomeStationId = site.HomeStationId!.Value,
                        Seed = unchecked(
                            (long)(CapsuleerProvisioningRules.Seed(seed, site.SystemId, CapsuleerCareer.Mining) & long.MaxValue)
                        ),
                        Version = CapsuleerPopulationStore.PopulationVersion,
                    }
                );
                world.SaveChanges();
            }
            foreach (var career in new[] { CapsuleerCareer.Mining, CapsuleerCareer.Hauling })
            {
                if (agents.Any(a => a.Career == career))
                {
                    continue;
                }
                ulong roleSeed = CapsuleerProvisioningRules.Seed(seed, site.SystemId, career);
                var origin = origins[(int)(roleSeed % (ulong)origins.Length)];
                CreateFounder(world, catalog, site, origin, career, seed);
            }
            transaction.Commit();
            created++;
        }
        return new(sites.Length, created, pending);
    }

    private void EnsureAccount(WorldDbContext world)
    {
        if (world.Set<Account>().Any(a => a.Id == CapsuleerCommunityStore.MinerAccountId))
        {
            return;
        }
        world.Add(
            new Account
            {
                Id = CapsuleerCommunityStore.MinerAccountId,
                UserName = "Capsuleer Community",
                CreatedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                CloneGrade = AccountCloneGrade.Omega,
            }
        );
        world.SaveChanges();
    }

    private void CreateFounder(
        WorldDbContext world,
        CatalogDbContext catalog,
        CapsuleerSite site,
        CapsuleerOrigin origin,
        CapsuleerCareer career,
        long seed
    )
    {
        long pilot = (world.Pilots.Max(p => (long?)p.Id) ?? 90000000) + 1;
        long ship = InventoryPersistence.NextId(world);
        int hull = career == CapsuleerCareer.Mining ? CapsuleerCommunityStore.VentureType : HaulingRules.BadgerType;
        int station = site.HomeStationId!.Value;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        string originalName = CapsuleerProvisioningRules.Name(seed, site.SystemId, career);
        string name = originalName;
        int suffix = 1;
        while (world.Pilots.Any(p => EF.Functions.Collate(p.Name, "NOCASE") == name))
        {
            name = originalName + "-" + suffix++;
        }
        int gender = (int)(CapsuleerProvisioningRules.Seed(seed, site.SystemId, career) % 2);
        CharacterRules.ValidateOrigin(
            new(name, origin.RaceId, origin.BloodlineId, gender, CharacterRules.UnspecifiedAncestry, [], [], origin.SchoolId),
            new(origin.RaceId, origin.RaceId, hull, origin.CorporationId, [station], null)
        );
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId,CreatedAt,RaceId,BloodlineId,
                GenderId,AncestryId,SchoolId)
            VALUES ({pilot},{CapsuleerCommunityStore.MinerAccountId},{name},{origin.CorporationId},{ship},{now},
                {origin.RaceId},{origin.BloodlineId},{gender},{CharacterRules.UnspecifiedAncestry},{origin.SchoolId})
            """
        );
        world.Database.ExecuteSql(
            $"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES ({ship},{pilot},{hull},{station},{name})"
        );
        StartingSkills.Grant(world, catalog, pilot, origin.RaceId);
        CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, hull, []);
        long module = 0;
        long cents = career == CapsuleerCareer.Mining ? CapsuleerCommunityStore.StartingCents : HaulingRules.StartingCents;
        if (career == CapsuleerCareer.Mining)
        {
            module = ship + 1;
            CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, (int)MiningLaserType.MinerI, []);
            world.Add(
                new OwnedItem
                {
                    Id = module,
                    OwnerId = pilot,
                    TypeId = (int)MiningLaserType.MinerI,
                    LocationId = station,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 1,
                }
            );
            world.Add(
                new SavedCapsuleerCommunity
                {
                    Id = site.SystemId,
                    PilotId = pilot,
                    Seed = (int)(CapsuleerProvisioningRules.Seed(seed, site.SystemId, career) % int.MaxValue),
                    SchoolId = origin.SchoolId,
                    Career = career,
                    HomeStationId = station,
                    FounderShipId = ship,
                    FounderModuleId = module,
                    StartingCents = cents,
                }
            );
        }
        else
        {
            world.Add(
                new SavedCapsuleerHauler
                {
                    Id = site.SystemId,
                    PilotId = pilot,
                    SourceStationId = station,
                    DestinationStationId = site.DestinationStationId!.Value,
                    FounderShipId = ship,
                    StartingCents = cents,
                }
            );
        }
        world.Add(new SavedHomeStation { PilotId = pilot, StationId = station });
        world.Add(new PilotWallet { PilotId = pilot, Cents = cents });
        world.SaveChanges();
        if (career == CapsuleerCareer.Mining)
        {
            PilotFittingStore.Fit(world, catalog, pilot, [module], station, (int)InventoryFlag.AutoFit, now);
            PilotFittingStore.SetOnline(world, catalog, pilot, ship, module, true, now);
        }
        CapsuleerPopulationStore.AddAgent(world, new(pilot, site.SystemId, career, ship, module, cents));
        long elapsed = world.Set<SavedCapsuleerClock>().Single().ElapsedTicks;
        var navigation = new NavigationState().Capture();
        if (career == CapsuleerCareer.Mining)
        {
            new MinerCheckpoint(
                pilot,
                new(MinerActivity.Docked, 0, 0, 0, 0, module, 0, navigation, ship, ActiveTicks: elapsed, ClockOffsetTicks: elapsed)
            ).Apply(world);
        }
        else
        {
            new HaulerCheckpoint(
                pilot,
                new(
                    HaulerActivity.Docked,
                    0,
                    0,
                    ship,
                    site.DestinationStationId!.Value,
                    0,
                    navigation,
                    ActiveTicks: elapsed,
                    ClockOffsetTicks: elapsed
                )
            ).Apply(world);
        }
        CapsuleerJobCheckpoint.Apply(
            world,
            new(
                pilot,
                0,
                0,
                CapsuleerProvisioningRules.FirstDueTicks(elapsed, seed, site.SystemId, career),
                career == CapsuleerCareer.Mining ? CapsuleerAction.Prepare : CapsuleerAction.Trade
            )
        );
    }
}
