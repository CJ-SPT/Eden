using System.Text.Json.Nodes;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Exploration;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Exploration;

public sealed partial class ProbeScanningTests
{
    [Fact]
    public void AutomaticSignaturesActivateAfterRestartAndPermitNativeWarpAndBookmarksWithoutProbes()
    {
        using var f = Prepare();
        _ = Load(f);
        WorldDatabase database = new(f.Db.Path);
        WorldConfigurationStore config = new(database, f.Clock);
        var original = config.Read();
        var legacy = JsonNode.Parse(config.Export(original.Active.Id))!;
        legacy["settings"]!.AsObject().Remove("exploration");
        var imported = config.Import(original.Generation, legacy.ToJsonString(), "legacy");
        Assert.False(imported.Pending!.Draft.Settings.Exploration.AlwaysResolvedSignatures);
        config.Validate(imported.Pending.Draft);
        Assert.Throws<InvalidDataException>(() => config.Validate(original.Active.Draft with { Settings = new() { Exploration = null! } }));
        config.Save(
            imported.Generation,
            original.Active.Draft with
            {
                Settings = original.Active.Draft.Settings with { Exploration = new(true) },
            },
            "admin"
        );
        Assert.False(database.Settings.Exploration.AlwaysResolvedSignatures);
        WorldInitialization.Initialize(database, f.Clock);
        Assert.True(database.Settings.Exploration.AlwaysResolvedSignatures);
        Assert.True(
            JsonNode.Parse(config.Export(config.Read().Active.Id))!["settings"]!["exploration"]![
                "alwaysResolvedSignatures"
            ]!.GetValue<bool>()
        );

        CombatAnomaly site;
        using (LiveWorld live = new(database, f.Clock, _ => { }))
        {
            var (scanning, sites) = Stores(f);
            site = Assert.Single(sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now));
            Assert.True(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
            Assert.True(scanning.Resolved(f.Db.Store.FindPilot(1000002)!.PilotId, site.Id, site.SystemId, f.Clock.Now));
            Assert.False(scanning.Resolved(f.PilotId, site.Id, site.SystemId + 1, f.Clock.Now));
            Assert.False(scanning.Resolved(f.PilotId, long.MaxValue, site.SystemId, f.Clock.Now));
            Assert.Empty(scanning.List(f.PilotId, site.SystemId, f.Clock.Now));
            using var scope = f.Db.Services.CreateScope();
            using var context = BookmarkContext(f, scope.ServiceProvider);
            var tracker = scope.ServiceProvider.GetRequiredService<CombatAnomalyHandler>();
            Assert.IsType<PilotCallResult.Success>(tracker.Call(context, "scanMgr", "SignalTrackerRegister", []));
            var automatic = Assert.Single(context.Notifications.OfType<AutomaticSignatureResults>());
            var arguments = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(automatic.Arguments)), 3);
            Assert.Empty(Assert.IsType<List<object?>>(arguments[0]));
            var result = Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(arguments[1])));
            var fields = Assert.IsType<Dictionary<string, object?>>(result.State);
            Assert.Equal(site.TargetId, System.Text.Encoding.UTF8.GetString(BlueRead.Bytes(fields["id"])));
            Assert.Equal(1d, fields["certainty"]);
            Assert.Equal(site.DungeonId, Convert.ToInt32(fields["dungeonID"]));
            Assert.Equal(ProbeScanningWire.Position(site.Position), Assert.IsType<object[]>(fields["data"]));
            Assert.Empty(Assert.IsType<List<object?>>(arguments[2]));
            context.Notifications.Clear();
            Assert.IsType<PilotCallResult.Success>(tracker.Call(context, "@systemScan", "GetFullState", []));
            Assert.Single(context.Notifications.OfType<AutomaticSignatureResults>());
            context.Notifications.Clear();
            tracker.Advance(context);
            Assert.Empty(context.Notifications.OfType<AutomaticSignatureResults>());

            var bookmarks = scope.ServiceProvider.GetRequiredService<IPilotBookmarks>();
            var folder = bookmarks.AddFolder(f.PilotId, "Automatic signatures", "");
            var bookmark = BookmarkReply(
                BookmarkCall(scope.ServiceProvider, context, [site.SystemId, "Site", "", site.TargetId, folder.Id, 0L])
            );
            Assert.NotNull(bookmark);
            Assert.Equal(site.Position, Assert.Single(bookmarks.Read(f.PilotId).Bookmarks).Target.Position);
            Dictionary<string, object?> warp = new() { ["minRange"] = 0d, ["fleet"] = false };
            Assert.IsType<PilotCallResult.Success>(
                scope
                    .ServiceProvider.GetRequiredService<FlightCallHandler>()
                    .Call(context, "beyonce", "CmdWarpToStuff", ["scan", site.TargetId], warp)
            );
            using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
            snapshot.Project();
            using WorldDbContext captured = new(snapshot.Database);
            Assert.Empty(captured.Set<SavedResolvedSignature>());
        }
        WorldInitialization.Initialize(database, f.Clock);
        Assert.True(database.Settings.Exploration.AlwaysResolvedSignatures);
        var enabled = config.Read();
        config.Save(enabled.Generation, original.Active.Draft, "disable automatic signatures");
        Assert.True(database.Settings.Exploration.AlwaysResolvedSignatures);
        WorldInitialization.Initialize(database, f.Clock);
        using LiveWorld reopened = new(database, f.Clock, _ => { });
        var (restoredScanning, restoredSites) = Stores(f);
        Assert.False(restoredScanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
        Assert.Contains(restoredSites.Signatures(site.SystemId, f.Clock.Now), current => current.Id == site.Id);
    }

    [Fact]
    public void AutomaticSignaturesRemoveRetiredResultsAndRevealReplacements()
    {
        using var f = Prepare();
        _ = Load(f);
        WorldDatabase database = new(f.Db.Path);
        WorldConfigurationStore config = new(database, f.Clock);
        var state = config.Read();
        config.Save(state.Generation, state.Active.Draft with { Settings = new() { Exploration = new(true) } }, "admin");
        WorldInitialization.Initialize(database, f.Clock);
        using LiveWorld live = new(database, f.Clock, _ => { });
        var (scanning, sites) = Stores(f);
        var site = Assert.Single(sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now));
        using var scope = f.Db.Services.CreateScope();
        using var context = BookmarkContext(f, scope.ServiceProvider);
        var tracker = scope.ServiceProvider.GetRequiredService<CombatAnomalyHandler>();
        tracker.Call(context, "scanMgr", "SignalTrackerRegister", []);
        context.Notifications.Clear();
        sites.Arrive(site.SystemId, site.Position, f.Clock.Now);
        long shipId = f.ShipId;
        for (int wave = 0; wave < 20 && sites.Signatures(site.SystemId, f.Clock.Now).Any(s => s.Id == site.Id); wave++)
        {
            using var owner = live.Enter();
            using WorldActionScope transaction = new(database);
            using CatalogDbContext catalog = new(f.CatalogPath);
            var world = transaction.World;
            var npcs = world.Set<WorldNpc>().Where(n => n.EncounterId == site.Id && n.DestroyedAt == null).ToArray();
            Assert.NotEmpty(npcs);
            foreach (var npc in npcs)
            {
                Assert.True(
                    NpcDamagePersistence
                        .Apply(
                            world,
                            catalog,
                            npc,
                            world.Pilots.Find(f.PilotId)!,
                            shipId,
                            new NpcProfileCatalog().Read(catalog, npc.TypeId).Attributes,
                            new(1e9, 1e9, 1e9, 1e9),
                            f.Clock.Now
                        )
                        .Destroyed
                );
            }
            transaction.Commit();
        }
        Assert.False(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
        tracker.Advance(context);
        var removed = Assert.Single(context.Notifications.OfType<AutomaticSignatureResults>());
        Assert.Empty(removed.Added);
        Assert.Equal(site.TargetId, Assert.Single(removed.Removed).TargetId);
        context.Notifications.Clear();
        f.Clock.Milliseconds += CombatAnomalyRules.ReplacementSeconds * 1000;
        tracker.Advance(context);
        var added = Assert.Single(context.Notifications.OfType<AutomaticSignatureResults>());
        var replacement = Assert.Single(added.Added);
        Assert.NotEqual(site.Id, replacement.Id);
        Assert.True(scanning.Resolved(f.PilotId, replacement.Id, replacement.SystemId, f.Clock.Now));
    }
}
