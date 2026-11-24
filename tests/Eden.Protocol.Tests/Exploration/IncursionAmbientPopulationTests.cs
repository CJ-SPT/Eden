using Eden.Data.Catalog;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Exploration;

public sealed class IncursionAmbientPopulationTests
{
    // The chosen period opens in the first configured high, low and null placements.
    private const long Tale = 5101;
    private const int High = 30003878;
    private const int HighIce = 30003876;
    private const int Low = 30002719;
    private const int Null = 30000798;
    private const int UnmappedNull = 30000298;
    private const int FirstGateId = 50090000;
    private const int SecondGateId = FirstGateId + 2;
    private const long CampWindow = 10 * TimeSpan.TicksPerMinute;
    private static readonly SpacePosition s_belt = new(1_000_000_000, 0, 0);
    private static readonly SpacePosition s_ice = new(1_500_000_000, 0, 0);
    private static readonly SpacePosition s_firstGate = new(3_000_000_000, 0, 0);
    private static readonly SpacePosition s_secondGate = new(5_000_000_000, 0, 0);

    [Theory]
    [InlineData(High, IncursionSecurity.High)]
    [InlineData(Low, IncursionSecurity.Low)]
    [InlineData(Null, IncursionSecurity.Null)]
    public void CampaignReplacesWarmOrdinaryBeltsAndRetiresSanshaAtTheExactEnd(int system, IncursionSecurity security)
    {
        using AmbientFixture fixture = new(system);
        using var live = fixture.Open();
        using var guard = GameplaySqlGuard.Begin();
        fixture.Review();
        var original = fixture.Contacts(NpcAnchorKind.Belt);
        Assert.NotEmpty(original);
        Assert.Empty(fixture.Contacts(NpcAnchorKind.IncursionBelt));
        Assert.Empty(fixture.Contacts(NpcAnchorKind.IncursionCamp));

        fixture.Now = IncursionRules.OpensAt(Tale);
        fixture.Review();
        var focus = Assert.Single(fixture.Directory.Read(fixture.Now), focus => focus.Systems.Any(entry => entry.SystemId == system));
        Assert.Equal(security, focus.Security);
        var replacement = fixture.Contacts(NpcAnchorKind.IncursionBelt);
        Assert.NotEmpty(replacement);
        Assert.All(replacement, contact => Assert.Equal((int)NpcPirateFaction.Sansha, contact.FactionId));
        Assert.Empty(fixture.Contacts(NpcAnchorKind.Belt));
        Assert.All(original, contact => Assert.False(fixture.Encounters.WasDestroyed(contact.Id)));
        Assert.Equal(security != IncursionSecurity.High, fixture.Contacts(NpcAnchorKind.IncursionCamp).Length > 0);
        Assert.Equal(Ids(replacement), Ids(fixture.Scene(NpcAnchorKind.IncursionBelt, s_belt)));

        fixture.Now = focus.EndsAt - 1;
        Assert.NotEmpty(fixture.Scene(NpcAnchorKind.IncursionBelt, s_belt));
        fixture.Now++;
        // Neither the warm scene nor the ordinary population waits for maintenance to review the expired focus.
        Assert.Empty(fixture.Scene(NpcAnchorKind.IncursionBelt, s_belt));
        Assert.Empty(fixture.Contacts(NpcAnchorKind.IncursionCamp));
        var restored = fixture.Contacts(NpcAnchorKind.Belt);
        Assert.NotEmpty(restored);
        Assert.All(restored, contact => Assert.Equal(original[0].FactionId, contact.FactionId));
        Assert.Equal(original.Select(contact => contact.TypeId).Order(), restored.Select(contact => contact.TypeId).Order());
        Assert.All(replacement, contact => Assert.False(fixture.Encounters.WasDestroyed(contact.Id)));
    }

    [Theory]
    [InlineData(Low)]
    [InlineData(Null)]
    [InlineData(UnmappedNull)]
    public async Task LowAndNullCampsRotateOnScheduleAndCheckpointWithoutDuplicating(int system)
    {
        using AmbientFixture fixture = new(system);
        long opens = IncursionRules.OpensAt(Tale);
        fixture.Now = opens;
        long[] firstIds;
        long firstGate;
        using (var live = fixture.Open())
        {
            using (GameplaySqlGuard.Begin())
            {
                fixture.Review();
                var groups = await Task.WhenAll(
                    Enumerable.Range(0, 3).Select(_ => Task.Run(() => fixture.Contacts(NpcAnchorKind.IncursionCamp)))
                );
                Assert.NotEmpty(groups[0]);
                firstIds = Ids(groups[0]);
                Assert.All(groups, camps => Assert.Equal(firstIds, Ids(camps)));
                firstGate = Assert.Single(groups[0].Select(camp => camp.AnchorId).Distinct());
                Assert.Contains(firstGate, new long[] { FirstGateId, SecondGateId });
                Assert.All(groups[0], camp => Assert.Equal((int)NpcPirateFaction.Sansha, camp.FactionId));
            }
            await live.FlushAsync();
        }

        using var reopened = fixture.Open();
        using var guard = GameplaySqlGuard.Begin();
        fixture.Now = opens + CampWindow - 1;
        fixture.Review();
        var before = fixture.Contacts(NpcAnchorKind.IncursionCamp);
        Assert.Equal(firstIds, Ids(before));
        long otherGate = firstGate == FirstGateId ? SecondGateId : FirstGateId;
        var firstPosition = firstGate == FirstGateId ? s_firstGate : s_secondGate;
        var otherPosition = firstGate == FirstGateId ? s_secondGate : s_firstGate;
        Assert.Equal(firstIds, Ids(fixture.Scene(NpcAnchorKind.IncursionCamp, firstPosition)));
        Assert.Empty(fixture.Scene(NpcAnchorKind.IncursionCamp, otherPosition));

        fixture.Now++;
        Assert.Empty(fixture.Scene(NpcAnchorKind.IncursionCamp, firstPosition));
        var after = fixture.Scene(NpcAnchorKind.IncursionCamp, otherPosition);
        Assert.NotEmpty(after);
        Assert.All(after, contact => Assert.Equal(otherGate, contact.AnchorId));
        Assert.Equal(Ids(after), Ids(fixture.Contacts(NpcAnchorKind.IncursionCamp)));
    }

    [Fact]
    public void ActiveMaterializedIceFieldHasItsOwnSanshaAndExpiresIndependentlyOfTheBelt()
    {
        using AmbientFixture fixture = new(HighIce);
        fixture.Now = IncursionRules.OpensAt(Tale);
        using var live = fixture.Open();
        using var guard = GameplaySqlGuard.Begin();
        fixture.Review();
        long expires = fixture.Now + TimeSpan.TicksPerMinute;
        using (var world = live.Economy.Begin(new(systems: [HighIce])))
        {
            world.Add(
                new SavedMiningSite
                {
                    Id = MiningSiteRules.FirstSiteId + 1,
                    SystemId = HighIce,
                    Family = MiningFamily.Ice,
                    DungeonId = 5499,
                    X = s_ice.X,
                    Y = s_ice.Y,
                    Z = s_ice.Z,
                    ExpiresAt = expires,
                }
            );
            world.Commit();
        }

        Assert.NotEmpty(fixture.Scene(NpcAnchorKind.IncursionBelt, s_ice));
        var belt = fixture.Scene(NpcAnchorKind.IncursionBelt, s_belt);
        Assert.NotEmpty(belt);
        fixture.Now = expires - 1;
        Assert.NotEmpty(fixture.Scene(NpcAnchorKind.IncursionBelt, s_ice));
        fixture.Now++;
        Assert.Empty(fixture.Scene(NpcAnchorKind.IncursionBelt, s_ice));
        Assert.Equal(Ids(belt), Ids(fixture.Scene(NpcAnchorKind.IncursionBelt, s_belt)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MothershipVictoryRestoresWarmBeltsAndRetiresCampsInTheSameTick(bool simulationObjects)
    {
        using AmbientFixture fixture = new(Low);
        using var live = fixture.Open(simulationObjects);
        using var guard = GameplaySqlGuard.Begin();
        var original = fixture.Scene(NpcAnchorKind.Belt, s_belt);
        Assert.NotEmpty(original);

        long victory = IncursionRules.OpensAt(Tale) + TimeSpan.TicksPerDay;
        fixture.Now = victory;
        fixture.Review();
        var focus = Assert.Single(fixture.Directory.Read(victory), focus => focus.Security == IncursionSecurity.Low);
        Assert.True(victory < focus.EndsAt);
        var camp = fixture.Contacts(NpcAnchorKind.IncursionCamp);
        var gate = Assert.Single(camp.Select(contact => contact.AnchorId).Distinct());
        var gatePosition = gate == FirstGateId ? s_firstGate : s_secondGate;
        Assert.Equal(Ids(camp), Ids(fixture.Scene(NpcAnchorKind.IncursionCamp, gatePosition)));
        Assert.NotEmpty(fixture.Scene(NpcAnchorKind.IncursionBelt, s_belt));

        int headquarters = Assert.Single(focus.Systems, system => system.SceneType == IncursionScene.Headquarters).SystemId;
        using (var world = live.Economy.Begin(new(systems: [headquarters])))
        {
            var progress = world.Set<SavedIncursionCampaign>().Find(focus.TaleId)!;
            progress.Clears = IncursionRules.ClearsToUnlockFinal;
            progress.HeadquartersCleared = true;
            world.Commit();
        }
        var flagship = Assert.Single(
            fixture.Sites.Incursions(headquarters, victory),
            site => site.DungeonId == IncursionSiteCatalog.Uroborus
        );
        using (CatalogDbContext catalog = fixture.OpenCatalog())
        using (var world = live.Economy.Begin(new(systems: focus.Systems.Select(system => system.SystemId).ToArray())))
        {
            var site = world.Set<WorldCombatAnomaly>().Find(flagship.Id)!;
            site.CompletedAt = victory;
            IncursionStore.CompleteSite(world, catalog, site, victory);
            Assert.Equal(victory, world.Set<SavedIncursionCampaign>().Find(focus.TaleId)!.DefeatedAt);
            world.Commit();
        }

        // Keep the same clock and skip maintenance to exercise the live population invalidation from settlement.
        Assert.Empty(fixture.Scene(NpcAnchorKind.IncursionCamp, gatePosition));
        Assert.Empty(fixture.Scene(NpcAnchorKind.IncursionBelt, s_belt));
        var restored = fixture.Scene(NpcAnchorKind.Belt, s_belt);
        Assert.Equal(original.Select(contact => contact.TypeId).Order(), restored.Select(contact => contact.TypeId).Order());
        Assert.All(camp, contact => Assert.False(fixture.Encounters.WasDestroyed(contact.Id)));
        Assert.False(Assert.Single(fixture.Directory.AgencyUpdates, update => update.TaleId == focus.TaleId).IsActive);
    }

    [Theory]
    [InlineData(NpcAnchorKind.IncursionBelt)]
    [InlineData(NpcAnchorKind.IncursionCamp)]
    public void ClearingAmbientSanshaUsesItsOwnCooldownWithoutBountiesOrCampaignCredit(NpcAnchorKind kind)
    {
        using AmbientFixture fixture = new(Low);
        long opens = IncursionRules.OpensAt(Tale);
        fixture.Now = opens + TimeSpan.TicksPerMinute;
        using var live = fixture.Open();
        using var guard = GameplaySqlGuard.Begin();
        fixture.Review();
        var cleared = fixture.Contacts(kind);
        Assert.NotEmpty(cleared);
        fixture.Kill(live, cleared);
        Assert.All(cleared, npc => Assert.True(fixture.Encounters.WasDestroyed(npc.Id)));
        Assert.Empty(fixture.Directory.PendingLoyalties(AmbientFixture.PilotId, fixture.Now));
        Assert.Equal(1, Assert.Single(fixture.Directory.Read(fixture.Now), focus => focus.Security == IncursionSecurity.Low).Influence);

        long respawn =
            kind == NpcAnchorKind.IncursionCamp
                ? opens + CampWindow
                : fixture.Now + NpcEncounterDefinitions.RespawnSeconds * TimeSpan.TicksPerSecond;
        fixture.Now = respawn - 1;
        Assert.Empty(fixture.Contacts(kind));
        fixture.Now++;
        var replacement = fixture.Contacts(kind);
        Assert.NotEmpty(replacement);
        Assert.Empty(Ids(replacement).Intersect(Ids(cleared)));
    }

    private static long[] Ids(IEnumerable<NpcContact> contacts) => contacts.Select(contact => contact.Id).Order().ToArray();

    private sealed class AmbientFixture : IDisposable
    {
        public const long PilotId = 90000001;

        private readonly int _system;
        private readonly TestClock _clock = new(DateTimeOffset.FromFileTime(IncursionRules.OpensAt(Tale) - 1));
        private readonly TestWorld _db;
        private readonly WorldDatabase _database;

        public AmbientFixture(int system)
        {
            _system = system;
            _db = new(clock: _clock, regionalNpcs: true);
            _database = _db.Services.GetRequiredService<WorldDatabase>();
            PassiveCatalogFixture.LoadNpcPopulation(_database.CatalogPath);
            PassiveCatalogFixture.LoadIncursions(_database.CatalogPath);
            PassiveCatalogFixture.LoadIncursionSecurity(_database.CatalogPath);
            if (system == HighIce)
            {
                PassiveCatalogFixture.LoadMining(_database.CatalogPath);
            }
            PrepareAnchors();
            _database.Catalog.PrepareRuntime();
            using CatalogDbContext catalog = new(_database.CatalogPath);
            ExplorationCatalogPreparation.Prepare(catalog);
            _db.Services.GetRequiredService<NpcPopulationSeeder>().WarmCatalog(_database);
        }

        public long Now
        {
            get => _clock.Now;
            set => _clock.UtcNow = DateTimeOffset.FromFileTime(value);
        }

        public IIncursionDirectory Directory => _db.Services.GetRequiredService<IIncursionDirectory>();
        public ICombatAnomalies Sites => _db.Services.GetRequiredService<ICombatAnomalies>();
        public INpcEncounters Encounters => _db.Services.GetRequiredService<INpcEncounters>();

        public LiveWorld Open(bool simulationObjects = true) => new(_database, _clock, _ => { }, simulationObjects: simulationObjects);

        public CatalogDbContext OpenCatalog() => new(_database.CatalogPath);

        public void Review() => _db.Services.GetRequiredService<IIncursionMaintenance>().Advance(Now);

        public NpcContact[] Contacts(NpcAnchorKind kind) =>
            Encounters.List(_system, Now).Where(contact => contact.AnchorKind == kind).ToArray();

        public NpcContact[] Scene(NpcAnchorKind kind, SpacePosition position) =>
            Encounters.ReadScene(_system, position, Now).Visible.Where(contact => contact.AnchorKind == kind).ToArray();

        public void Kill(LiveWorld live, IReadOnlyList<NpcContact> contacts)
        {
            using CatalogDbContext catalog = new(_database.CatalogPath);
            using var world = live.Economy.Begin(new(systems: [_system], pilots: [PilotId]));
            var pilot = world.Pilots.Find(PilotId)!;
            foreach (var contact in contacts)
            {
                var npc = world.Set<WorldNpc>().Find(contact.Id)!;
                var attributes = PilotFittingStore.ReadAttributes(catalog, npc.TypeId);
                var result = NpcDamagePersistence.Apply(
                    world,
                    catalog,
                    npc,
                    pilot,
                    pilot.ActiveShip.Id,
                    attributes,
                    new(1_000_000, 0, 0, 0),
                    Now,
                    bypassResistances: true
                );
                Assert.True(result.Destroyed);
                Assert.Null(result.Reward);
            }
            world.Commit();
        }

        private void PrepareAnchors()
        {
            using CatalogDbContext catalog = new(_database.CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemGroup VALUES (9,'Asteroid Belt',2);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES (15,'Belt',9,0,1);
                """
            );
            int belt = Enumerable.Range(40200000, 100).First(id => NpcSpawnRules.Occupied(NpcSpawnRules.Seed(_system, id)));
            AddBody(belt, _system, 15, AsteroidDefinitions.BeltGroup, s_belt);
            AddGate(FirstGateId, 30000142, s_firstGate);
            AddGate(SecondGateId, 30000144, s_secondGate);
            catalog.SaveChanges();

            void AddGate(int id, int destination, SpacePosition position)
            {
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO Stargate(Id,SolarSystemId,DestinationId)
                    VALUES ({id},{_system},{id + 1}),({id + 1},{destination},{id});
                    """
                );
                AddBody(id, _system, 16, 10, position);
                AddBody(id + 1, destination, 16, 10, position);
            }

            void AddBody(int id, int system, int type, int group, SpacePosition position) =>
                catalog.Add(
                    new Celestial
                    {
                        Id = id,
                        SolarSystemId = system,
                        TypeId = type,
                        GroupId = group,
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z,
                    }
                );
        }

        public void Dispose() => _db.Dispose();
    }
}
