using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Exploration;

public sealed class CombatAnomalyTests
{
    [Fact]
    public void DockedUnsubscribedTicksDoNotLoadSitesButRegistrationAndUndockingStillDo()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true, useTestClock: true);
        PassiveCatalogFixture.LoadCombatAnomalies(new WorldDatabase(f.Db.Path).CatalogPath);
        f.Db.Store.SetPosition(f.Pilot.ShipId, null, f.Pilot.SystemId);
        var pilot = f.Pilot;
        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        CombatAnomalyStore sites = new(new(f.Db.Path));
        CombatAnomalyHandler handler = new(sites, f.Db.Services.GetRequiredService<Eden.Gameplay.Agents.IPilotMissions>(), f.Clock);
        using (var metrics = SessionPerformance.Begin("docked exploration", _ => { }, force: true)!)
        {
            for (int i = 0; i < 3; i++)
            {
                handler.Advance(context);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
            Assert.Empty(context.Notifications);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<WorldCombatAnomaly>());
        }
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "scanMgr", "SignalTrackerRegister", []));
        Assert.NotEmpty(Assert.Single(context.Notifications.OfType<SignalTrackerFullState>()).Sites);
        context.Notifications.Clear();
        handler.Advance(context);
        Assert.DoesNotContain(context.Notifications, n => n is SignalTrackerFullState);
        var site = Assert.Single(sites.List(pilot.SystemId, f.Clock.Now));
        f.Db.Store.SetPosition(pilot.ShipId, site.Position, pilot.SystemId);
        context.ChangeShip(f.Pilot);
        context.Flight.Snapshot();
        CombatAnomalyHandler unregistered = new(sites, f.Db.Services.GetRequiredService<Eden.Gameplay.Agents.IPilotMissions>(), f.Clock);
        unregistered.Advance(context);
        Assert.True(Assert.Single(sites.List(pilot.SystemId, f.Clock.Now)).Started);
    }

    private sealed class EmptyAsteroids : IAsteroidField
    {
        public IReadOnlyList<StaticSpaceObject> List(int systemId) => [];
    }

    private static CombatAnomalyStore Prepare(NpcCombatTests.Fixture f)
    {
        PassiveCatalogFixture.LoadNpcPopulation(new WorldDatabase(f.Db.Path).CatalogPath);
        PassiveCatalogFixture.LoadCombatAnomalies(new WorldDatabase(f.Db.Path).CatalogPath);
        CombatAnomalyStore sites = new(new(f.Db.Path));
        var site = Assert.Single(sites.List(CombatAnomalyRules.SystemId, f.Clock.Now));
        using WorldDbContext world = new(f.Db.Path);
        // Preserve the schema-109 Hideaway contract through the content expansion.
        world.Set<WorldCombatAnomaly>().Find(site.Id)!.DefinitionVersion = 0;
        world.SaveChanges();
        CombatAnomalyStore.Invalidate(world);
        _ = sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now);
        return sites;
    }

    [Fact]
    public void SharedArrivalWavesCompletionAndReplacementSurviveReopen()
    {
        using NpcCombatTests.Fixture f = new();
        var sites = Prepare(f);
        var site = Assert.Single(sites.List(CombatAnomalyRules.SystemId, f.Clock.Now));
        Assert.False(site.Started);
        Assert.Contains(site.Objects, o => o.TypeId == CombatAnomalyRules.TelescopeTypeId);
        sites.Arrive(site.SystemId, site.Position + new SpacePosition(CombatAnomalyRules.ArrivalRange + 1, 0, 0), f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<WorldNpc>().Where(n => n.AnchorKind == NpcAnchorKind.Anomaly));
        }
        sites.Arrive(site.SystemId, site.Position, f.Clock.Now);
        sites.Arrive(site.SystemId, site.Position, f.Clock.Now);
        int killed = 0;
        long reward = 0;
        for (int wave = 0; wave < 4; wave++)
        {
            using (WorldDbContext world = new(f.Db.Path))
            {
                Assert.Equal(wave, world.Set<WorldCombatAnomaly>().Single(s => !s.IsSignature).Wave);
                Assert.Equal(
                    CombatAnomalyRules.Wave(site.Id, wave).Count,
                    world.Set<WorldNpc>().Count(n => n.EncounterId == site.Id && n.DestroyedAt == null)
                );
            }
            long[] ids;
            using (WorldDbContext world = new(f.Db.Path))
            {
                ids = world.Set<WorldNpc>().Where(n => n.EncounterId == site.Id && n.DestroyedAt == null).Select(n => n.Id).ToArray();
            }
            foreach (long id in ids)
            {
                using WorldDbContext world = new(f.Db.Path);
                using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
                using var transaction = world.Database.BeginTransaction();
                var npc = world.Set<WorldNpc>().Find(id)!;
                var attributes = new NpcProfileCatalog().Read(catalog, npc.TypeId).Attributes;
                var result = NpcDamagePersistence.Apply(
                    world,
                    catalog,
                    npc,
                    world.Pilots.Find(NpcCombatTests.Fixture.PilotId)!,
                    NpcCombatTests.Fixture.ShipId,
                    attributes,
                    new DamageVector(1e9, 1e9, 1e9, 1e9),
                    f.Clock.Now
                );
                Assert.True(result.Destroyed);
                Assert.NotNull(result.Reward);
                reward += result.Reward!.AmountCents;
                world.SaveChanges();
                transaction.Commit();
                killed++;
            }
            if (wave == 1)
            {
                WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
                sites = new(new(f.Db.Path));
                Assert.True(Assert.Single(sites.List(site.SystemId, f.Clock.Now)).Started);
            }
        }
        Assert.Empty(sites.List(site.SystemId, f.Clock.Now));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        sites = new(new(f.Db.Path));
        Assert.Empty(sites.List(site.SystemId, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(killed, world.Set<WorldWreck>().Count());
            Assert.Equal(reward, world.Set<NpcBounty>().Sum(b => b.AmountCents));
        }
        long deadline = f.Clock.Now + CombatAnomalyRules.ReplacementSeconds * TimeSpan.TicksPerSecond;
        Assert.Empty(sites.List(site.SystemId, deadline - 1));
        var replacement = Assert.Single(sites.List(site.SystemId, deadline));
        Assert.NotEqual(site.Id, replacement.Id);
        Assert.NotEqual(site.TargetId, replacement.TargetId);
        Assert.False(replacement.Started);
    }

    [Fact]
    public void WarmScannerAndAbsentSystemReadsAvoidSqlAndFittingWork()
    {
        using NpcCombatTests.Fixture f = new();
        var sites = Prepare(f);
        var site = Assert.Single(sites.List(CombatAnomalyRules.SystemId, f.Clock.Now));
        using var metrics = SessionPerformance.Begin("anomaly-read", _ => { }, force: true)!;
        for (int i = 0; i < 20; i++)
        {
            Assert.Same(site, Assert.Single(sites.List(site.SystemId, f.Clock.Now + i)));
            Assert.Empty(sites.List(site.SystemId + 1, f.Clock.Now));
            sites.Arrive(site.SystemId, SpacePosition.Zero, f.Clock.Now);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void RolledBackLastKillKeepsSavedWave()
    {
        using NpcCombatTests.Fixture f = new();
        var sites = Prepare(f);
        var site = Assert.Single(sites.List(CombatAnomalyRules.SystemId, f.Clock.Now));
        sites.Arrive(site.SystemId, site.Position, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        using (world.Database.BeginTransaction())
        {
            foreach (var npc in world.Set<WorldNpc>().Where(n => n.EncounterId == site.Id).ToArray())
            {
                npc.DestroyedAt = f.Clock.Now;
                CombatAnomalyStore.Killed(world, catalog, npc, f.Clock.Now);
            }
            world.SaveChanges();
        }
        Assert.True(Assert.Single(sites.List(site.SystemId, f.Clock.Now)).Started);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<WorldCombatAnomaly>().Single(s => !s.IsSignature).Wave);
        Assert.Equal(
            CombatAnomalyRules.Wave(site.Id, 0).Count,
            saved.Set<WorldNpc>().Count(n => n.EncounterId == site.Id && n.DestroyedAt == null)
        );
    }

    [Fact]
    public void ScannerBindingFullStateAndRemovalUsePinnedWireShapes()
    {
        using NpcCombatTests.Fixture f = new();
        var sites = Prepare(f);
        SharedSpace space = new();
        using PilotCallContext context = new(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            p => new(f.Db.Store, p, f.Clock, space, []),
            42,
            SignedSettings.Empty
        );
        context.Select(f.Pilot);
        CombatAnomalyHandler handler = new(sites, f.Db.Services.GetRequiredService<Eden.Gameplay.Agents.IPilotMissions>(), f.Clock);
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "scanMgr", "SignalTrackerRegister", []));
        var notification = Assert.IsType<SignalTrackerFullState>(Assert.Single(context.Notifications));
        Assert.Equal(3, notification.Arguments.Length);
        var state = Assert.IsType<object[]>(new SignalTrackerState(sites.List(context.Pilot.SystemId, f.Clock.Now)).ToBlue());
        Assert.Equal(4, state.Length);
        Assert.IsType<AnomalyMap>(state[0]);
        var site = Assert.Single(sites.List(context.Pilot.SystemId, f.Clock.Now));
        var wireState = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(state)), 4);
        var map = Assert.IsType<Dictionary<long, object?>>(wireState[0]);
        var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(map[site.Id]).State);
        Assert.Equal(1136L, fields["scanStrengthAttribute"]);
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "scanMgr", "GetSystemScanMgr", []));
        Assert.Equal("@systemScan", context.ResolveReference(context.Reference("@systemScan", PilotBindingLifetime.Location).Id));
        context.ReleaseLocationReferences();
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "@systemScan", "GetFullState", []));
        context.Notifications.Clear();
        using (WorldDbContext world = new(f.Db.Path))
        {
            var saved = world.Set<WorldCombatAnomaly>().Find(site.Id)!;
            saved.CompletedAt = f.Clock.Now;
            saved.ReplaceAt = f.Clock.Now + TimeSpan.TicksPerMinute;
            world.SaveChanges();
            CombatAnomalyStore.Invalidate(world);
        }
        handler.Advance(context);
        Assert.Equal(site.Id, Assert.Single(Assert.IsType<SignalTrackerAnomalyUpdate>(Assert.Single(context.Notifications)).Removed));
    }

    [Theory]
    [InlineData("item")]
    [InlineData("scan")]
    public void WarpEntryPointsResolveCurrentSiteAndRejectStaleTargets(string subject)
    {
        using NpcCombatTests.Fixture f = new();
        var sites = Prepare(f);
        var site = Assert.Single(sites.List(f.Pilot.SystemId, f.Clock.Now));
        SharedSpace space = new();
        using PilotCallContext context = new(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            p => new(f.Db.Store, p, f.Clock, space, []),
            42,
            SignedSettings.Empty
        );
        context.Select(f.Pilot);
        context.Flight.Snapshot();
        using var scope = f.Db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<FlightCallHandler>();
        object target = subject == "item" ? site.Id : site.TargetId;
        Dictionary<string, object?> keywords = new() { ["minRange"] = 10000d, ["fleet"] = false };
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "beyonce", "CmdWarpToStuff", [subject, target], keywords));
        using PilotCallContext otherSystem = new(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), []),
            43,
            SignedSettings.Empty
        );
        otherSystem.Select(f.Pilot with { CurrentSystem = new(site.SystemId + 1, 20000020, 10000002) });
        Assert.IsType<PilotCallResult.Failure>(handler.Call(otherSystem, "beyonce", "CmdWarpToStuff", [subject, target], keywords));
        object missing = subject == "item" ? site.Id + 1 : "EDN-MISSING";
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "beyonce", "CmdWarpToStuff", [subject, missing], keywords));
        var parsed = FlightRequestWire.Read("beyonce", "CmdWarpToStuff", [subject, target], keywords).Command;
        Assert.Equal(
            10000d,
            subject == "item" ? Assert.IsType<FlightCommand.WarpTo>(parsed).Range : Assert.IsType<FlightCommand.WarpToScan>(parsed).Range
        );
    }

    [Fact]
    public async Task SimultaneousArrivalsCreateOneDefendingFleet()
    {
        using NpcCombatTests.Fixture f = new();
        var sites = Prepare(f);
        var site = Assert.Single(sites.List(f.Pilot.SystemId, f.Clock.Now));
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => sites.Arrive(site.SystemId, site.Position, f.Clock.Now))));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(CombatAnomalyRules.Wave(site.Id, 0).Count, world.Set<WorldNpc>().Count(n => n.EncounterId == site.Id));
        Assert.Equal(0, world.Set<WorldCombatAnomaly>().Single(s => !s.IsSignature).Wave);
    }

    [Fact]
    public void ResidentContactsPublishAnomalyNpcsAndCheckpointTheirMembership()
    {
        using NpcCombatTests.Fixture f = new(liveWorld: true);
        var sites = Prepare(f);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        _ = encounters.List(f.Pilot.SystemId, f.Clock.Now);
        var site = Assert.Single(sites.List(f.Pilot.SystemId, f.Clock.Now));
        sites.Arrive(site.SystemId, site.Position, f.Clock.Now);
        var contacts = encounters.List(site.SystemId, f.Clock.Now).Where(n => n.Id >= CombatAnomalyRules.FirstNpcId).ToArray();
        Assert.Equal(CombatAnomalyRules.Wave(site.Id, 0).Count, contacts.Length);
        Assert.All(contacts, n => Assert.Contains(n.TypeId, CombatAnomalyRules.Wave(site.Id, 0)));
        f.Db.Services.GetRequiredService<LiveWorld>().Dispose();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(
            contacts.Select(n => n.Id).Order(),
            world.Set<WorldNpc>().Where(n => n.EncounterId == site.Id).Select(n => n.Id).Order()
        );
    }

    [Fact]
    public async Task SimultaneousFatalHitsSettleOneKillAndOneReinforcement()
    {
        using NpcCombatTests.Fixture f = new();
        var sites = Prepare(f);
        var site = Assert.Single(sites.List(f.Pilot.SystemId, f.Clock.Now));
        sites.Arrive(site.SystemId, site.Position, f.Clock.Now);
        var database = new WorldDatabase(f.Db.Path);
        long npcId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            npcId = world.Set<WorldNpc>().Single(n => n.EncounterId == site.Id).Id;
        }
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        lock (database.MotionLock)
                        {
                            using WorldDbContext world = new(f.Db.Path);
                            using CatalogDbContext catalog = new(database.CatalogPath);
                            using var transaction = world.Database.BeginTransaction();
                            var npc = world.Set<WorldNpc>().Find(npcId)!;
                            try
                            {
                                var profile = new NpcProfileCatalog().Read(catalog, npc.TypeId);
                                NpcDamagePersistence.Apply(
                                    world,
                                    catalog,
                                    npc,
                                    world.Pilots.Find(NpcCombatTests.Fixture.PilotId)!,
                                    NpcCombatTests.Fixture.ShipId,
                                    profile.Attributes,
                                    new DamageVector(1e9, 1e9, 1e9, 1e9),
                                    f.Clock.Now
                                );
                                world.SaveChanges();
                                transaction.Commit();
                                return true;
                            }
                            catch (InvalidDataException)
                            {
                                return false;
                            }
                        }
                    })
                )
        );
        Assert.Single(results, success => success);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<WorldWreck>());
        Assert.Single(saved.Set<NpcBounty>());
        Assert.Equal(1, saved.Set<WorldCombatAnomaly>().Single(s => !s.IsSignature).Wave);
        Assert.Equal(
            CombatAnomalyRules.Wave(site.Id, 1).Count,
            saved.Set<WorldNpc>().Count(n => n.EncounterId == site.Id && n.DestroyedAt == null)
        );
    }

    [Fact]
    public async Task NormalSessionTickActivatesSiteAfterAuthoritativePositionAdvance()
    {
        using NpcCombatTests.Fixture f = new(useTestClock: true);
        var sites = Prepare(f);
        var site = Assert.Single(sites.List(f.Pilot.SystemId, f.Clock.Now));
        SharedSpace space = new();
        foreach (long account in new[] { 1000001L, 1000002L })
        {
            var pilot = f.Db.Store.FindPilot(account)!;
            f.Db.Store.SetPosition(pilot.ShipId, site.Position, site.SystemId);
            pilot = f.Db.Store.FindPilot(account)!;
            using var scope = f.Db.Services.CreateScope();
            using PilotCallContext context = new(
                account,
                pilot.Account,
                [pilot],
                p => new(f.Db.Store, p, f.Clock, space, []),
                account,
                SignedSettings.Empty
            );
            context.Select(pilot);
            context.Flight.Snapshot();
            CombatAnomalyHandler handler = new(sites, f.Db.Services.GetRequiredService<Eden.Gameplay.Agents.IPilotMissions>(), f.Clock);
            handler.Call(context, "scanMgr", "SignalTrackerRegister", []);
            var fullState = Assert.IsType<SignalTrackerFullState>(Assert.Single(context.Notifications));
            Assert.Equal(site.Id, Assert.Single(fullState.Sites).Id);
            PilotSessionTick tick = new(
                f.Clock,
                scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
                scope.ServiceProvider.GetRequiredService<SkillHandler>(),
                new EmptyAsteroids(),
                anomalies: handler
            );
            PilotSessionOutput output = new(context, account, account, _ => Task.CompletedTask, _ => { });
            await tick.AdvanceAsync(context, output, _ => { }, account, account);
        }
        Assert.True(Assert.Single(sites.List(site.SystemId, f.Clock.Now)).Started);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(CombatAnomalyRules.Wave(site.Id, 0).Count, world.Set<WorldNpc>().Count(n => n.EncounterId == site.Id));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void ScannerUpdateSerializesThroughSessionPacketWithNativeRemovalSet(bool addSite, int removedCount)
    {
        CombatAnomaly site = new(
            CombatAnomalyRules.FirstSiteId,
            "EDN-001",
            CombatAnomalyRules.SystemId,
            CombatAnomalyRules.DungeonId,
            110912,
            1,
            SpacePosition.Zero,
            false,
            []
        );
        var removed = Enumerable.Range(0, removedCount).Select(i => site.Id + i).ToArray();
        SignalTrackerAnomalyUpdate notification = new(site.SystemId, addSite ? [site] : [], removed);
        var packet = PilotSessionWire.Notification(1000001, 42, notification);
        Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet)));
        var arguments = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)));
        Assert.Equal(site.SystemId, Assert.IsType<long>(arguments[0]));
        Assert.Equal(removed.Order(), Assert.IsType<HashSet<long>>(arguments[2]).Order());
        if (addSite)
        {
            var sites = Assert.IsType<Dictionary<long, object?>>(arguments[1]);
            var fields = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(sites[site.Id]).State);
            Assert.Equal(site.Id, fields["instanceID"]);
            Assert.Equal(site.TargetId, BlueMarshal.Text(fields["targetID"]));
            Assert.Equal(1136L, fields["scanStrengthAttribute"]);
        }
    }
}
