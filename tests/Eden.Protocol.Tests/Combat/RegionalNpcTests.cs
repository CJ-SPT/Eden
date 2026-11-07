using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class RegionalNpcTests
{
    private const int System = 30000142;
    private const int FirstBelt = 40200000;
    private const int BeltCount = 8;
    private const long Now = 134000000000000000;

    internal static void Prepare(TestWorld db, double security = 0.7, NpcRegion region = NpcRegion.TheForge)
    {
        string path = new WorldDatabase(db.Path).CatalogPath;
        PassiveCatalogFixture.LoadNpcPopulation(path);
        using CatalogDbContext catalog = new(path, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemGroup VALUES (9,'Asteroid Belt',2);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES (15,'Belt',9,0,1);
            """
        );
        if (catalog.Set<Region>().Find((int)region) is null)
        {
            catalog.Add(new Region { Id = (int)region, Name = region.ToString() });
        }
        var system = catalog.Set<SolarSystem>().AsTracking().Include(s => s.Constellation).Single(s => s.Id == System);
        system.Constellation.RegionId = (int)region;
        catalog.SaveChanges();
        string data = $"{{\"securityStatus\":{security.ToString(global::System.Globalization.CultureInfo.InvariantCulture)}}}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{data})");
        for (int index = 0; index < BeltCount; index++)
        {
            catalog.Add(
                new Celestial
                {
                    Id = FirstBelt + index,
                    SolarSystemId = System,
                    TypeId = 15,
                    GroupId = 9,
                    X = 10000000000 + index * 10000000,
                }
            );
        }
        catalog.SaveChanges();
    }

    [Fact]
    public void PinnedCatalogHasUsablePoolsAcrossEveryOrdinaryFactionAndHull()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        var profiles = db.Services.GetRequiredService<NpcSpawnCatalog>();
        Assert.Equal(22, NpcSpecialTypes.Excluded.Count);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath))
        {
            Assert.All(NpcSpecialTypes.Excluded, id => Assert.NotNull(catalog.Set<ItemType>().Find(id)));
        }
        Assert.DoesNotContain(profiles.Profiles, profile => NpcSpecialTypes.Excluded.Contains(profile.TypeId));
        Assert.DoesNotContain(profiles.Profiles, profile => profile.Name.StartsWith("TEST ", StringComparison.OrdinalIgnoreCase));
        Assert.True(profiles.Profiles.Count > 300, $"Only {profiles.Profiles.Count} ordinary NPC profiles admitted.");
        Assert.Contains(profiles.Profiles, profile => profile.TypeId == 25632);
        foreach (var group in NpcPirateGroups.All)
        {
            Assert.NotEmpty(profiles.Pool(group.Faction, group.Hull));
        }
        foreach (var profile in profiles.Profiles)
        {
            Assert.Contains(
                NpcPirateGroups.All,
                g => g.GroupId == profile.GroupId && g.Faction == profile.Faction && g.Hull == profile.Hull
            );
            Assert.True(profile.CorporationId > 0);
            Assert.True(profile.WreckTypeId > 0);
            Assert.True(profile.Attributes[DogmaAttributeId.StructureHitPoints] > 0);
            Assert.True(profile.Mass > 0);
            Assert.True(profile.Radius > 0);
        }
    }

    [Theory]
    [InlineData(0.949, NpcSecurityBand.Safest)]
    [InlineData(0.85, NpcSecurityBand.Safest)]
    [InlineData(0.849, NpcSecurityBand.High)]
    [InlineData(0.45, NpcSecurityBand.High)]
    [InlineData(0.449, NpcSecurityBand.Low)]
    [InlineData(0.001, NpcSecurityBand.Low)]
    [InlineData(0, NpcSecurityBand.Null)]
    [InlineData(-1, NpcSecurityBand.Null)]
    public void DisplayedSecurityChoosesThePopulationBand(double security, NpcSecurityBand band) =>
        Assert.Equal(band, NpcSpawnRules.SecurityBand(security));

    [Fact]
    public void RegionMappingCoversOrdinarySpaceAndExcludesSpecialRegions()
    {
        Assert.Equal(64, Enum.GetValues<NpcRegion>().Length);
        Assert.All(Enum.GetValues<NpcRegion>(), region => Assert.NotNull(NpcRegions.Pirates((int)region)));
        Assert.Equal(NpcPirateFaction.Serpentis, NpcRegions.Pirates((int)NpcRegion.Fade));
        Assert.Equal(NpcPirateFaction.RogueDrones, NpcRegions.Pirates((int)NpcRegion.Oasa));
        int[] excluded = [10000004, 10000017, 10000019, 10000070, 10001000, 11000001, 19000001];
        Assert.All(excluded, region => Assert.Null(NpcRegions.Pirates(region)));
    }

    [Theory]
    [InlineData(NpcRegion.TheForge, 0.7, NpcPirateFaction.Guristas)]
    [InlineData(NpcRegion.Domain, 0.3, NpcPirateFaction.Sansha)]
    [InlineData(NpcRegion.Fade, -0.5, NpcPirateFaction.Serpentis)]
    [InlineData(NpcRegion.Malpais, -0.5, NpcPirateFaction.RogueDrones)]
    public async Task ConcurrentVisitorsSharePersistentRegionalGroups(NpcRegion region, double security, NpcPirateFaction faction)
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db, security, region);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => encounters.List(System, Now))));
        Assert.NotEmpty(results[0]);
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.All(
            results[0],
            npc =>
            {
                Assert.Equal((int)faction, npc.FactionId);
                Assert.True(npc.Id >= NpcEncounterDefinitions.FirstNpcId);
                Assert.NotEqual(NpcEncounterDefinitions.GuristasRookieType, npc.TypeId);
            }
        );
        _ = WorldInitialization.Open(db.Path);
        Assert.Equal(results[0], encounters.List(System, Now + TimeSpan.TicksPerSecond));
        using WorldDbContext world = new(db.Path);
        Assert.Equal(BeltCount, world.Set<WorldNpcEncounter>().Count());
        foreach (var group in world.Set<WorldNpc>().AsEnumerable().GroupBy(n => n.EncounterId))
        {
            int expected = NpcSpawnRules.Composition(NpcSpawnRules.SecurityBand(security), NpcSpawnRules.Seed(System, group.Key)).Count;
            Assert.Equal(expected, group.Count());
        }
    }

    [Fact]
    public void SafestSystemsRetireOldRookiesWithoutKillsOrRewards()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db, 0.949);
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new WorldNpc
                {
                    Id = NpcEncounterDefinitions.FirstNpcId,
                    TypeId = NpcEncounterDefinitions.GuristasRookieType,
                    SystemId = System,
                    ShieldCharge = 7,
                    ArmorDamage = 3,
                }
            );
            world.SaveChanges();
        }
        Assert.Empty(db.Services.GetRequiredService<INpcEncounters>().List(System, Now));
        using WorldDbContext saved = new(db.Path);
        var retired = Assert.Single(saved.Set<WorldNpc>());
        Assert.Equal(Now, retired.RetiredAt);
        Assert.Null(retired.DestroyedAt);
        Assert.Equal(7, retired.ShieldCharge);
        Assert.Equal(3, retired.ArmorDamage);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<WorldWreck>());
    }

    [Theory]
    [InlineData((int)NpcCloneSoldierType.BloodTransporter, false)]
    [InlineData((int)NpcCloneSoldierType.BloodTransporter, true)]
    [InlineData((int)NpcDevelopmentType.TestDrainer, false)]
    [InlineData((int)NpcDevelopmentType.TestDrainer, true)]
    [InlineData((int)NpcDevelopmentType.TestAttacker, false)]
    [InlineData((int)NpcDevelopmentType.TestAttacker, true)]
    public void ExcludedNpcsRetireWithoutRewardsAndOnlyEmptyGroupsAreReplaced(int excludedTypeId, bool survivor)
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db, -0.5);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        _ = encounters.List(System, Now);
        long encounterId;
        long retiredId;
        long allyId;
        int generation;
        using (WorldDbContext world = new(db.Path))
        {
            var group = world.Set<WorldNpc>().AsEnumerable().GroupBy(n => n.EncounterId).First().ToArray();
            encounterId = group[0].EncounterId;
            retiredId = group[0].Id;
            allyId = group[1].Id;
            generation = world.Set<WorldNpcEncounter>().Find(encounterId)!.Generation;
            group[0].TypeId = excludedTypeId;
            foreach (var npc in group.Skip(survivor ? 2 : 1))
            {
                npc.DestroyedAt = Now;
            }
            world.Add(NpcAggression.Start(retiredId, 1000000000001, 0, Now));
            world.Add(NpcAggression.Start(allyId, 1000000000001, 0, Now));
            foreach (
                var source in new[]
                {
                    (ShipId: retiredId, Kind: MissileSourceKind.Npc),
                    (ShipId: allyId, Kind: MissileSourceKind.Npc),
                    (ShipId: retiredId, Kind: MissileSourceKind.Pilot),
                }
            )
            {
                world.Add(
                    new WorldMissile
                    {
                        PilotId = 90000001,
                        SourceShipId = source.ShipId,
                        SourceKind = source.Kind,
                        Status = MissileFlightStatus.Flying,
                        Snapshot = "{}",
                    }
                );
            }
            world.SaveChanges();
        }
        long activation = Now + TimeSpan.TicksPerSecond;
        var contacts = encounters.List(System, activation);
        Assert.DoesNotContain(contacts, n => n.Id == retiredId || NpcSpecialTypes.Excluded.Contains(n.TypeId));
        using (WorldDbContext saved = new(db.Path))
        {
            var retired = saved.Set<WorldNpc>().Find(retiredId)!;
            Assert.Equal(activation, retired.RetiredAt);
            Assert.Null(retired.DestroyedAt);
            Assert.DoesNotContain(saved.Set<NpcAggression>(), a => a.NpcId == retiredId);
            Assert.Contains(saved.Set<NpcAggression>(), a => a.NpcId == allyId);
            var cancelled = saved.Set<WorldMissile>().Single(m => m.SourceShipId == retiredId && m.SourceKind == MissileSourceKind.Npc);
            Assert.Equal(MissileFlightStatus.Expired, cancelled.Status);
            Assert.Equal(activation, cancelled.EndedAt);
            Assert.Equal(2, saved.Set<WorldMissile>().Count(m => m.Status == MissileFlightStatus.Flying));
            var members = contacts.Where(n => saved.Set<WorldNpc>().Find(n.Id)!.EncounterId == encounterId).ToArray();
            Assert.Equal(
                survivor ? 1 : NpcSpawnRules.Composition(NpcSecurityBand.Null, NpcSpawnRules.Seed(System, encounterId)).Count,
                members.Length
            );
            Assert.Equal(generation, saved.Set<WorldNpcEncounter>().Find(encounterId)!.Generation);
            Assert.Empty(saved.Set<NpcBounty>());
            Assert.Empty(saved.Set<WorldWreck>());
            Assert.Empty(saved.Set<ShipLoss>());
        }
        _ = WorldInitialization.Open(db.Path);
        Assert.Equal(contacts.Select(n => n.Id), encounters.List(System, activation + TimeSpan.TicksPerSecond).Select(n => n.Id));
    }

    [Fact]
    public void ClearingAGroupStartsOneSavedRespawnAndDoesNotRerollItsLoot()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var initial = encounters.List(System, Now);
        using WorldDbContext world = new(db.Path);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        long encounterId = world.Set<WorldNpc>().First().EncounterId;
        var members = world.Set<WorldNpc>().Where(n => n.EncounterId == encounterId).OrderBy(n => n.Id).ToArray();
        var pilot = world.Pilots.Find(90000001L)!;
        foreach (var npc in members)
        {
            using var transaction = world.Database.BeginTransaction();
            var attributes = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
            Assert.True(
                NpcDamagePersistence
                    .Apply(world, catalog, npc, pilot, pilot.ActiveShipId, attributes, new(1000000000, 0, 0, 0), Now)
                    .Destroyed
            );
            world.SaveChanges();
            transaction.Commit();
        }
        long respawn = Now + TimeSpan.FromSeconds(NpcEncounterDefinitions.RespawnSeconds).Ticks;
        Assert.Equal(respawn, world.Set<WorldNpcEncounter>().Find(encounterId)!.NextSpawnAt);
        var loot = world
            .Set<OwnedItem>()
            .Where(i => i.LocationId >= Eden.Gameplay.Loot.WreckDefinitions.FirstWreckId)
            .OrderBy(i => i.Id)
            .Select(i => new
            {
                i.Id,
                i.TypeId,
                i.Quantity,
                i.LocationId,
            })
            .ToArray();
        Assert.DoesNotContain(encounters.List(System, respawn - 1), n => members.Select(m => m.Id).Contains(n.Id));
        _ = WorldInitialization.Open(db.Path);
        var replacement = encounters.List(System, respawn);
        Assert.DoesNotContain(replacement, n => members.Select(m => m.Id).Contains(n.Id));
        using WorldDbContext reopened = new(db.Path);
        Assert.NotEmpty(reopened.Set<WorldNpc>().Where(n => n.EncounterId == encounterId && n.DestroyedAt == null));
        Assert.Equal(
            loot,
            reopened
                .Set<OwnedItem>()
                .Where(i => i.LocationId >= Eden.Gameplay.Loot.WreckDefinitions.FirstWreckId)
                .OrderBy(i => i.Id)
                .Select(i => new
                {
                    i.Id,
                    i.TypeId,
                    i.Quantity,
                    i.LocationId,
                })
                .ToArray()
        );
        Assert.Equal(members.Length, reopened.Set<NpcBounty>().Count());
    }

    [Theory]
    [InlineData(NpcPirateFaction.Guristas, 210)]
    [InlineData(NpcPirateFaction.Angels, 185)]
    [InlineData(NpcPirateFaction.BloodRaiders, 246)]
    [InlineData(NpcPirateFaction.Sansha, 246)]
    [InlineData(NpcPirateFaction.Serpentis, 222)]
    [InlineData(NpcPirateFaction.RogueDrones, 15331)]
    public void LootUsesFactionPoolsAndProbabilityBoundaries(NpcPirateFaction faction, int ammunition)
    {
        var drops = NpcLootRules.Roll(faction, NpcHullClass.Frigate, 59, 34, 0);
        Assert.Equal(2, drops.Count);
        Assert.Equal(ammunition, drops[0].TypeId);
        Assert.Empty(NpcLootRules.Roll(faction, NpcHullClass.Frigate, 60, 35, 0));
        Assert.All(drops, drop => Assert.True(drop.Quantity > 0));
    }

    [Fact]
    public void SurvivingGroupMembersPreventRespawnAfterTheUsualDelay()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        encounters.List(System, Now);
        long encounterId;
        long defeated;
        int originalCount;
        using (WorldDbContext world = new(db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            var group = world.Set<WorldNpc>().AsEnumerable().GroupBy(n => n.EncounterId).OrderByDescending(g => g.Count()).First();
            originalCount = group.Count();
            Assert.True(originalCount > 1);
            var npc = group.First();
            defeated = npc.Id;
            encounterId = npc.EncounterId;
            var pilot = world.Pilots.Find(90000001L)!;
            NpcDamagePersistence.Apply(
                world,
                catalog,
                npc,
                pilot,
                pilot.ActiveShipId,
                PilotFittingStore.ReadAttributes(catalog, npc.TypeId),
                new(1000000000, 0, 0, 0),
                Now
            );
            world.SaveChanges();
            transaction.Commit();
        }
        encounters.List(System, Now + TimeSpan.FromSeconds(NpcEncounterDefinitions.RespawnSeconds + 1).Ticks);
        using WorldDbContext saved = new(db.Path);
        var members = saved.Set<WorldNpc>().Where(n => n.EncounterId == encounterId).ToArray();
        Assert.Equal(originalCount, members.Length);
        Assert.Equal(originalCount - 1, members.Count(n => n.DestroyedAt is null));
        Assert.NotNull(members.Single(n => n.Id == defeated).DestroyedAt);
        Assert.Null(saved.Set<WorldNpcEncounter>().Find(encounterId)!.NextSpawnAt);
    }

    [Fact]
    public void MissingSecurityMetadataProducesNoSubstitutePopulation()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Set<SourceRecord>().Where(r => r.Dataset == "client/systems" && r.Key == "30000142").ExecuteDelete();
        }
        Assert.Empty(db.Services.GetRequiredService<INpcEncounters>().List(System, Now));
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<WorldNpcEncounter>());
        Assert.Empty(saved.Set<WorldNpc>());
    }
}
