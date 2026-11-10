using Eden.Data.World.Exploration;
using Eden.Data.World.Mining;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Exploration;

public sealed class AgencyTests
{
    [Fact]
    public void IceDiscoveryKeepsUnknownFieldsSeparateAndTracksDepletionReplacementAndReopen()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadMining(f.Database.CatalogPath);
        int system = AddIceSystem(f.Database.CatalogPath);
        var directory = f.Db.Services.GetRequiredService<IAgencyDirectory>();
        var available = directory.Ice(f.Clock.Now);
        Assert.Empty(available.Known);
        Assert.Equal(1, Assert.Single(available.Available, s => s.SystemId == system).Count);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<SavedMiningSite>());
            Assert.Empty(world.Set<WorldCombatAnomaly>());
        }
        MiningSiteStore sites = new(f.Database);
        var site = Assert.Single(sites.Anomalies(system, f.Clock.Now));
        var reply = Assert.IsType<Dictionary<long, object?>>(f.Reply("dungeonInstanceCacheMgr", "GetIceBeltInstances"));
        var fields = Fields(Assert.Single(Assert.IsType<List<object?>>(reply[system])));
        Assert.Equal(site.Id, fields["siteID"]);
        Assert.Equal(site.Id, fields["instanceID"]);
        Assert.Equal(site.TargetId, BlueMarshal.Text(fields["targetID"]));
        Assert.Equal((long)MiningSiteRules.IceArchetype, fields["archetypeID"]);
        Assert.Equal((long)site.DungeonId, fields["dungeonID"]);
        Assert.Equal((long)site.NameId, fields["dungeonNameID"]);
        Assert.Equal(new object?[] { site.Position.X, site.Position.Y, site.Position.Z }, Assert.IsType<object?[]>(fields["position"]));
        long due = f.Clock.Now + MiningSiteRules.IceReplacementDelay;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedMiningSite>().Find(site.Id)!.ReplaceAt = due;
            world.SaveChanges();
        }
        Assert.Empty(directory.Ice(due - 1).Known);
        Assert.Equal(0, Assert.Single(directory.Ice(due - 1).Available, s => s.SystemId == system).Count);
        Assert.Equal(1, Assert.Single(directory.Ice(due).Available, s => s.SystemId == system).Count);
        Assert.Empty(directory.Ice(due).Known);
        // Discovery reports that a replacement is due, without creating it.
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(site.Id, Assert.Single(world.Set<SavedMiningSite>()).Id);
        }
        var replacement = Assert.Single(sites.Anomalies(system, due));
        Assert.NotEqual(site.Id, replacement.Id);
        WorldInitialization.Open(f.Db.Path);
        var reopened = new AgencyDirectory(new(f.Db.Path)).Ice(due);
        Assert.Equal(replacement.Id, Assert.Single(Assert.Single(reopened.Known).Sites).Id);
        Assert.Equal(1, Assert.Single(reopened.Available, s => s.SystemId == system).Count);
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void SignatureCountsIncludeCombatHackingAndGasWithoutLeakingOrRecreatingRetiredSites()
    {
        using Fixture f = new();
        int system = CombatAnomalyRules.SystemId;
        long now = f.Clock.Now;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.AddRange(
                new WorldCombatAnomaly
                {
                    Id = 1,
                    SystemId = system,
                    Slot = 0,
                    IsSignature = true,
                },
                new WorldCombatAnomaly
                {
                    Id = 2,
                    SystemId = system,
                    Slot = 0,
                    IsSignature = true,
                },
                new WorldCombatAnomaly
                {
                    Id = 3,
                    SystemId = system,
                    Slot = 1,
                    IsSignature = true,
                },
                new WorldCombatAnomaly
                {
                    Id = 4,
                    SystemId = system,
                    Slot = 1,
                    IsSignature = true,
                    CompletedAt = now,
                },
                new WorldCombatAnomaly
                {
                    Id = 5,
                    SystemId = system,
                    Slot = 2,
                },
                new SavedExplorationSite
                {
                    Id = 1,
                    SystemId = system,
                    Kind = 0,
                    ExpiresAt = now + 1,
                },
                new SavedExplorationSite
                {
                    Id = 2,
                    SystemId = system,
                    Kind = 1,
                    ExpiresAt = now + 1,
                },
                new SavedExplorationSite
                {
                    Id = 3,
                    SystemId = system,
                    Kind = 1,
                    ExpiresAt = now + 1,
                    ReplaceAt = now + 1,
                },
                new SavedMiningSite
                {
                    Id = 1,
                    SystemId = system,
                    Family = MiningFamily.Gas,
                    ExpiresAt = now + 1,
                },
                new SavedMiningSite
                {
                    Id = 2,
                    SystemId = system,
                    Family = MiningFamily.Ice,
                    ExpiresAt = long.MaxValue,
                }
            );
            world.SaveChanges();
        }
        var counts = Assert.IsType<Dictionary<long, object?>>(f.Reply("dungeonInstanceCacheMgr", "GetSignatureInstances"));
        Assert.Equal(3L, Assert.Single(counts).Value);
        Assert.Equal((long)system, Assert.Single(counts).Key);
        var directory = f.Db.Services.GetRequiredService<IAgencyDirectory>();
        Assert.Equal(1, Assert.Single(directory.Signatures(now + 1)).Count);
        WorldTuningTests.Activate(f.Db, s => s with { NpcPopulation = s.NpcPopulation with { Pirates = false } });
        Assert.Equal(1, Assert.Single(directory.Signatures(now)).Count);
        Assert.Empty(directory.Signatures(now + 1));
        WorldInitialization.Open(f.Db.Path);
        Assert.Empty(new AgencyDirectory(new(f.Db.Path)).Signatures(now + 1));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(5, saved.Set<WorldCombatAnomaly>().Count());
        Assert.Equal(3, saved.Set<SavedExplorationSite>().Count());
        Assert.Equal(2, saved.Set<SavedMiningSite>().Count());
        Assert.Empty(f.Context.Notifications);
    }

    public static TheoryData<string, string> ReadMethods =>
        new()
        {
            { "dungeonInstanceCacheMgr", "GetSignatureInstances" },
            { "dungeonInstanceCacheMgr", "GetIceBeltInstances" },
            { "dungeonInstanceCacheMgr", "GetIceBeltsCount" },
            { "dungeonInstanceCacheMgr", "GetCombatAnomalyInstances" },
            { "dungeonInstanceCacheMgr", "GetCombatAnomaliesCount" },
            { "dungeonInstanceCacheMgr", "GetOreAnomalyInstances" },
            { "dungeonInstanceCacheMgr", "GetOreAnomaliesCount" },
            { "dungeonInstanceCacheMgr", "GetOreAnomaliesCountInRange" },
            { "dungeonInstanceCacheMgr", "GetBulwarkMiningSiteCounts" },
            { "dungeonInstanceCacheMgr", "GetFactionWarfareInstances" },
            { "dungeonInstanceCacheMgr", "GetFactionWarfareBattlefieldInstances" },
            { "dungeonInstanceCacheMgr", "GetHomefrontSiteInstances" },
            { "dungeonInstanceCacheMgr", "GetPirateInsurgencyInstances" },
            { "dungeonInstanceCacheMgr", "GetPirateInsurgencyIceHeistInstances" },
            { "dungeonInstanceCacheMgr", "GetTriglavianSiteInstances" },
            { "custom_agency_provider", "get_content_data" },
            { "map", "GetIncursionGlobalReport" },
            { "map", "GetSystemsInIncursions" },
            { "dynamicResourceCacheMgr", "GetESSAgencyData" },
            { "dynamicResourceCacheMgr", "GetDynamicResourceSettings" },
            { "dynamicResourceCacheMgr", "GetDBSMapData" },
        };

    [Theory]
    [MemberData(nameof(ReadMethods))]
    public void ProvidersRequireSelectionRejectForgedFiltersAndPreserveNativeCollections(string service, string method)
    {
        using Fixture f = new(select: false);
        Assert.Same(PilotCallResult.Unsupported, f.Call(service, method));
        f.Context.Select(f.Db.Store.FindPilot(1000001)!);
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, method, [90000002L]));
        Assert.IsType<PilotCallResult.Failure>(f.Call(service, method, keywords: new() { ["characterID"] = 90000002L }));
        object? reply = f.Reply(service, method, new() { ["machoVersion"] = 1L });
        if (method == "GetDynamicResourceSettings")
        {
            var settings = Assert.IsType<Dictionary<string, object?>>(reply);
            // ESSFiltersCont uses these as percent-edit bounds even with no banks.
            double minimum = Assert.IsType<double>(settings["minOutput"]);
            double maximum = Assert.IsType<double>(settings["maxOutput"]);
            Assert.True(minimum < maximum);
            Assert.Equal(1d, minimum);
            Assert.Equal(2d, maximum);
            Assert.Equal(86400L, settings["timeIntervalSeconds"]);
            Assert.Equal(9900L, settings["autopaymentIntervalSeconds"]);
            Assert.Equal(360L, settings["bufferTimeSeconds"]);
            Assert.Equal(60L, settings["reservePulseIntervalSeconds"]);
            Assert.Equal(0.4, settings["bountiesCut"]);
            Assert.Equal(0.975, settings["mainBankPayRatio"]);
            Assert.Equal(15000000L, settings["reserveBankMaxPayout"]);
            Assert.Equal(3L, settings["reserveBankTimeAccessFactor"]);
        }
        else if (method is "get_content_data" or "GetIncursionGlobalReport")
        {
            Assert.Empty(Assert.IsType<List<object?>>(reply));
        }
        else if (method == "GetSystemsInIncursions")
        {
            var fields = Fields(reply);
            Assert.Equal(
                new[] { "locationID", "sceneType", "templateNameID" },
                Assert.IsType<List<object?>>(fields["header"]).Select(BlueMarshal.Text)
            );
            Assert.Empty(Assert.IsType<List<object?>>(fields["lines"]));
        }
        else
        {
            Assert.Empty(Assert.IsType<Dictionary<string, object?>>(reply));
        }
        Assert.Same(PilotCallResult.Unsupported, f.Call(service, "UnknownAgencyMethod"));
        Assert.Empty(f.Context.Notifications);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedMiningSite>());
        Assert.Empty(world.Set<SavedExplorationSite>());
    }

    private static int AddIceSystem(string path)
    {
        int system = MiningSiteCatalog.Sites.Single(s => s.DungeonId == 5497).Systems![0];
        using CatalogDbContext catalog = new(path, writable: true);
        catalog.Add(
            new Eden.Data.Catalog.SolarSystem
            {
                Id = system,
                Name = "Agency ice system",
                ConstellationId = catalog.Set<Eden.Data.Catalog.Constellation>().First().Id,
            }
        );
        var anchor = catalog.Set<Eden.Data.Catalog.Celestial>().AsNoTracking().First();
        anchor.Id = system + 20000000;
        anchor.SolarSystemId = system;
        catalog.Add(anchor);
        catalog.SaveChanges();
        return system;
    }

    private static Dictionary<string, object?> Fields(object? value) => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State);

    private sealed class Fixture : IDisposable
    {
        internal TestClock Clock { get; } = new();
        internal TestWorld Db { get; }
        internal WorldDatabase Database { get; }
        internal PilotCallContext Context { get; }
        private readonly IServiceScope _scope;

        internal Fixture(bool select = true)
        {
            Db = new(clock: Clock);
            Database = Db.Services.GetRequiredService<WorldDatabase>();
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, new(), Db.Objects), 42, SignedSettings.Empty);
            if (select)
            {
                Context.Select(pilot);
            }
        }

        internal PilotCallResult Call(
            string service,
            string method,
            object?[]? args = null,
            Dictionary<string, object?>? keywords = null
        ) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args ?? [])), _ => { }, keywords);

        internal object? Reply(string service, string method, Dictionary<string, object?>? keywords = null) =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(service, method, keywords: keywords)).Reply));

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
