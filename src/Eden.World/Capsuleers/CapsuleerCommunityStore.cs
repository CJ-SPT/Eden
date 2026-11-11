using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable]
internal sealed partial class CapsuleerCommunityStore(WorldDatabase database) : ICapsuleerCommunity
{
    internal const long MinerAccountId = -1;
    internal const int VentureType = 32880;
    internal const long StartingCents = 1000000;

    internal static void Configure(ModelBuilder model)
    {
        CapsuleerProvisioningSchema.Configure(model);
        var community = model.Entity<SavedCapsuleerCommunity>();
        community.ToTable("WorldCapsuleerCommunity").HasKey(c => c.Id);
        community.HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        var hauler = model.Entity<SavedCapsuleerHauler>();
        hauler.ToTable("WorldCapsuleerHauler").HasKey(h => h.Id);
        hauler.HasOne<Pilot>().WithMany().HasForeignKey(h => h.PilotId);
    }

    internal static void Initialize(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCapsuleerCommunity(
                Id INTEGER PRIMARY KEY CHECK(Id=1), Seed INTEGER NOT NULL, SchoolId INTEGER NOT NULL,
                Career INTEGER NOT NULL, PilotId INTEGER REFERENCES WorldPilot(Id), Progress TEXT,
                HomeStationId INTEGER NOT NULL, FounderShipId INTEGER NOT NULL, FounderModuleId INTEGER NOT NULL,
                StartingCents INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldCapsuleerHauler(
                Id INTEGER PRIMARY KEY CHECK(Id=1), PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id),
                SourceStationId INTEGER NOT NULL, DestinationStationId INTEGER NOT NULL,
                FounderShipId INTEGER NOT NULL, StartingCents INTEGER NOT NULL, Progress TEXT);
            """
        );
    }

    internal static void Provision(WorldDbContext world, CatalogDbContext catalog, TimeProvider clock)
    {
        if (world.Set<SavedCapsuleerCommunity>().Any())
        {
            ProvisionHauler(world, catalog, clock);
            return;
        }
        var human = world
            .Pilots.Include(p => p.ActiveShip)
            .Where(p => p.AccountId > 0)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .FirstOrDefault();
        if (human is null || catalog.FittingType(VentureType) is null || catalog.FittingType((int)MiningLaserType.MinerI) is null)
        {
            return;
        }
        int home = world.Set<SavedHomeStation>().Find(human.Id)?.StationId ?? human.ActiveShip.StationId;
        int? station = Home(world, catalog, home, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        if (station is null)
        {
            return;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        world.Add(
            new Account
            {
                Id = MinerAccountId,
                UserName = "Capsuleer Community",
                CreatedAt = now,
                CloneGrade = AccountCloneGrade.Omega,
            }
        );
        world.SaveChanges();
        long id = (world.Pilots.Max(p => (long?)p.Id) ?? 90000000) + 1;
        long ship = InventoryPersistence.NextId(world);
        int corporation = checked(
            (int)(
                catalog.Read(CatalogData.Client.Schools, (long)human.SchoolId)?.CorporationID
                ?? catalog.RequireStation(station.Value).OwnerId
            )
        );
        string name = "Ari Vartio";
        int suffix = 1;
        while (world.Pilots.Any(p => p.Name == name))
        {
            name = "Ari Vartio " + suffix++;
        }
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId,CreatedAt,RaceId,BloodlineId,
                GenderId,AncestryId,SchoolId)
            VALUES ({id},{MinerAccountId},{name},{corporation},{ship},{now},{human.RaceId},{human.BloodlineId},
                {human.GenderId},{human.AncestryId},{human.SchoolId})
            """
        );
        world.Database.ExecuteSql(
            $"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES ({ship},{id},{VentureType},{station.Value},{name})"
        );
        StartingSkills.Grant(world, catalog, id, human.RaceId);
        GrantRequirements(world, catalog, id, VentureType, []);
        GrantRequirements(world, catalog, id, (int)MiningLaserType.MinerI, []);
        world.Add(
            new OwnedItem
            {
                Id = ship + 1,
                OwnerId = id,
                TypeId = (int)MiningLaserType.MinerI,
                LocationId = station.Value,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = 1,
            }
        );
        world.Add(new SavedHomeStation { PilotId = id, StationId = station.Value });
        world.Add(new PilotWallet { PilotId = id, Cents = StartingCents });
        world.Add(
            new SavedCapsuleerCommunity
            {
                Id = 1,
                PilotId = id,
                Seed = checked((int)(id % int.MaxValue)),
                SchoolId = human.SchoolId,
                Career = CapsuleerCareer.Mining,
                HomeStationId = station.Value,
                FounderShipId = ship,
                FounderModuleId = ship + 1,
                StartingCents = StartingCents,
            }
        );
        world.SaveChanges();
        ProvisionHauler(world, catalog, clock);
    }

    private static void ProvisionHauler(WorldDbContext world, CatalogDbContext catalog, TimeProvider clock)
    {
        if (world.Set<SavedCapsuleerHauler>().Any() || catalog.FittingType(HaulingRules.BadgerType) is null)
        {
            return;
        }
        var community = world.Set<SavedCapsuleerCommunity>().SingleOrDefault(c => c.Id == 1);
        if (community?.PilotId is not long minerId)
        {
            return;
        }
        int source = community.HomeStationId;
        var home = catalog.RequireStation(source);
        if (!StationCapabilities.Has(catalog, home.OperationId, StationService.Market))
        {
            return;
        }
        int system = home.SolarSystemId;
        var destination = catalog
            .GatesInSystem(system)
            .OrderBy(g => g.DestinationSystemId)
            .Where(g => catalog.Read(CatalogData.Client.Solarsystemcontent, (long)g.DestinationSystemId)?.Security >= 0.45)
            .Where(g =>
                catalog
                    .GatesInSystem(g.DestinationSystemId)
                    .Any(exit => exit.Id == g.DestinationId && exit.DestinationId == g.Id && exit.DestinationSystemId == system)
            )
            .SelectMany(g => catalog.StationsInSystem(g.DestinationSystemId).OrderBy(station => station.Id))
            .FirstOrDefault(station => StationCapabilities.Has(catalog, station.OperationId, StationService.Market));
        if (destination is null)
        {
            return;
        }
        var miner = world.Pilots.Find(minerId)!;
        long pilot = world.Pilots.Max(p => p.Id) + 1;
        long ship = InventoryPersistence.NextId(world);
        string name = "Kira Auvinen";
        int suffix = 1;
        while (world.Pilots.Any(p => p.Name == name))
        {
            name = "Kira Auvinen " + suffix++;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId,CreatedAt,RaceId,BloodlineId,
                GenderId,AncestryId,SchoolId)
            VALUES ({pilot},{MinerAccountId},{name},{miner.CorporationId},{ship},{now},{miner.RaceId},{miner.BloodlineId},
                {miner.GenderId},{miner.AncestryId},{miner.SchoolId})
            """
        );
        world.Database.ExecuteSql(
            $"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES ({ship},{pilot},{HaulingRules.BadgerType},{source},{name})"
        );
        StartingSkills.Grant(world, catalog, pilot, miner.RaceId);
        GrantRequirements(world, catalog, pilot, HaulingRules.BadgerType, []);
        world.Add(new SavedHomeStation { PilotId = pilot, StationId = source });
        world.Add(new PilotWallet { PilotId = pilot, Cents = HaulingRules.StartingCents });
        world.Add(
            new SavedCapsuleerHauler
            {
                Id = 1,
                PilotId = pilot,
                FounderShipId = ship,
                SourceStationId = source,
                DestinationStationId = destination.Id,
                StartingCents = HaulingRules.StartingCents,
            }
        );
        world.SaveChanges();
    }

    internal static void GrantRequirements(WorldDbContext world, CatalogDbContext catalog, long pilot, int type, HashSet<int> visited)
    {
        if (!visited.Add(type))
        {
            return;
        }
        var attributes = catalog.FittingAttributes(type);
        foreach (var (skillAttribute, levelAttribute) in InventoryDefinitions.RequiredSkills)
        {
            int skill = (int)attributes.GetValueOrDefault(skillAttribute);
            if (skill == 0)
            {
                continue;
            }
            GrantRequirements(world, catalog, pilot, skill, visited);
            long points = TrainedSkill.PointsForLevel(
                catalog.FittingAttributes(skill)[DogmaAttributeId.SkillTimeConstant],
                (int)attributes.GetValueOrDefault(levelAttribute)
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilotSkill(PilotId,TypeId,Points) VALUES ({pilot},{skill},{points})
                ON CONFLICT(PilotId,TypeId) DO UPDATE SET Points=MAX(Points,excluded.Points)
                """
            );
        }
    }

    private static int? Home(WorldDbContext world, CatalogDbContext catalog, int home, long now)
    {
        int origin = catalog.RequireStation(home).SolarSystemId;
        HashSet<int> seen = [origin];
        List<int> level = [origin];
        while (level.Count > 0)
        {
            foreach (int system in level.Order())
            {
                if (
                    catalog.Read(CatalogData.Client.Solarsystemcontent, (long)system)?.Security is not >= 0.45
                    || !catalog.CelestialsInSystem(system).Any(c => c.GroupId == AsteroidDefinitions.BeltGroup)
                )
                {
                    continue;
                }
                var stations = catalog.StationsInSystem(system).OrderBy(s => s.Id == home ? 0 : 1).ThenBy(s => s.Id);
                if (stations.FirstOrDefault() is { } station)
                {
                    AsteroidFieldStore.Populate(world, catalog, system, now);
                    world.SaveChanges();
                    if (world.Set<WorldAsteroid>().Any(a => a.SystemId == system && a.Quantity > 0))
                    {
                        return station.Id;
                    }
                }
            }
            level = level
                .SelectMany(system => catalog.Definitions.GatesInSystem(system))
                .Select(g => g.DestinationSystemId)
                .Where(seen.Add)
                .ToList();
        }
        return null;
    }

    public IReadOnlyList<CapsuleerIdentity> Pilots() =>
        WorldActionScope.Inspect(
            database,
            world =>
                world
                    .Set<SavedCapsuleerCommunity>()
                    .Where(c => c.PilotId != null)
                    .Select(c => new CapsuleerIdentity(c.PilotId!.Value, c.Seed, c.SchoolId, c.Career))
                    .AsEnumerable()
                    .Concat(
                        world
                            .Set<SavedCapsuleerHauler>()
                            .AsEnumerable()
                            .Select(h => new CapsuleerIdentity(h.PilotId, 0, 0, CapsuleerCareer.Hauling))
                    )
                    .ToArray()
        );
}
