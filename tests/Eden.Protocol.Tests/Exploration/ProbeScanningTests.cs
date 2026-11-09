using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class ProbeScanningTests
{
    [Fact]
    public void GasSignatureResolvesWithNativeLadarFieldsAndArrivalDoesNotCreateACombatEncounter()
    {
        using var f = Prepare();
        PassiveCatalogFixture.LoadMining(f.CatalogPath);
        long module = Load(f);
        WorldDatabase db = new(f.Db.Path);
        CombatAnomalyStore sites = new(db, mining: new Eden.World.Mining.MiningSiteStore(db));
        PilotProbeScanningStore scanning = new(db, sites);
        var site = Assert.Single(
            sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now),
            s => s.Mining == Eden.Gameplay.Mining.MiningFamily.Gas
        );
        var probes = scanning.Launch(f.PilotId, module, 8, f.Clock.Now).Probes;
        var scan = scanning.Begin(f.PilotId, Formation(probes, site.Position), f.Clock.Now);
        f.Clock.Milliseconds += (scan.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        var result = Assert.Single(scanning.Complete(f.PilotId, scan, f.Clock.Now), r => r.Site.Id == site.Id);
        Assert.Equal(1, result.Certainty);
        var fields = Assert.IsType<Dictionary<string, object?>>(ProbeScanningWire.Result(result).State);
        Assert.Equal(Eden.Gameplay.Mining.MiningSiteRules.GasStrengthAttribute, fields["strengthAttributeID"]);
        Assert.Equal(Eden.Gameplay.Mining.MiningSiteRules.GasArchetype, fields["archetypeID"]);
        Assert.True(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
        sites.Arrive(site.SystemId, site.Position, f.Clock.Now);
        using WorldDbContext world = new(f.Db.Path);
        Assert.DoesNotContain(world.Set<WorldNpc>(), n => n.EncounterId == site.Id);
    }

    private static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadProbes(f.CatalogPath);
        PassiveCatalogFixture.LoadNpcPopulation(f.CatalogPath);
        PassiveCatalogFixture.LoadCombatAnomalies(f.CatalogPath);
        f.Train(3402, 5);
        return f;
    }

    private static (PilotProbeScanningStore Scanning, CombatAnomalyStore Sites) Stores(Fixture f)
    {
        WorldDatabase db = new(f.Db.Path);
        CombatAnomalyStore sites = new(db);
        return (new(db, sites), sites);
    }

    private static long Load(Fixture f, int launcher = 17938, int probe = 30013, int quantity = 8, bool explicitSystem = true)
    {
        long module = f.Fit(launcher);
        long stack = f.Grant(probe, quantity);
        f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 10000;
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(f.ShipId, new(pilot.X + 1_000_000, pilot.Y, pilot.Z), explicitSystem ? CombatAnomalyRules.SystemId : null);
        return module;
    }

    private static ProbePlacement[] Formation(IReadOnlyList<ScanProbe> probes, SpacePosition center, int step = 1) =>
        ProbeScanningRuleTests
            .Formation(center, step: step)
            .Take(probes.Count)
            .Select((p, i) => p.Placement with { Id = probes[i].Id })
            .ToArray();

    [Fact]
    public void LaunchScanResolveCombatRewardAndReplacementSurviveRestart()
    {
        using var f = Prepare();
        long module = Load(f);
        var (scanning, sites) = Stores(f);
        var site = Assert.Single(sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now));
        Assert.DoesNotContain(sites.List(site.SystemId, f.Clock.Now), s => s.Id == site.Id);
        var launch = scanning.Launch(f.PilotId, module, 8, f.Clock.Now);
        Assert.Equal(8, launch.Probes.Count);
        Assert.InRange(launch.Probes[0].Profile.Strength, 40, 100);
        Assert.Equal(1, site.Difficulty);
        Assert.Equal(0, launch.Magazine.Quantity);
        Assert.False(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
        var wide = scanning.Begin(f.PilotId, Formation(launch.Probes, site.Position, step: 8), f.Clock.Now);
        Assert.Empty(scanning.Complete(f.PilotId, wide, wide.ReadyAt - 1));
        f.Clock.Milliseconds += (wide.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        var wideResult = Assert.Single(scanning.Complete(f.PilotId, wide, f.Clock.Now));
        Assert.InRange(wideResult.Certainty, 0.001, 0.25);
        Assert.False(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
        var narrow = scanning.Begin(f.PilotId, Formation(launch.Probes, site.Position), f.Clock.Now);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        (scanning, sites) = Stores(f);
        Assert.Equal(narrow.ReadyAt, scanning.Pending(f.PilotId)!.ReadyAt);
        f.Clock.Milliseconds += (narrow.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        var result = Assert.Single(scanning.Complete(f.PilotId, narrow, f.Clock.Now));
        Assert.Equal(1, result.Certainty);
        Assert.Equal(site.Position, result.Position);
        Assert.Empty(scanning.Complete(f.PilotId, narrow, f.Clock.Now));
        Assert.True(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
        Assert.Equal(site.TargetId, Assert.Single(sites.Signatures(site.SystemId, f.Clock.Now)).TargetId);

        sites.Arrive(site.SystemId, site.Position, f.Clock.Now);
        int killed = 0;
        long reward = 0;
        for (int wave = 0; wave < 20 && sites.Signatures(site.SystemId, f.Clock.Now).Any(s => s.Id == site.Id); wave++)
        {
            using WorldDbContext world = new(f.Db.Path);
            using CatalogDbContext catalog = new(f.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            var npcs = world.Set<WorldNpc>().Where(n => n.EncounterId == site.Id && n.DestroyedAt == null).ToArray();
            Assert.NotEmpty(npcs);
            foreach (var npc in npcs)
            {
                var outcome = NpcDamagePersistence.Apply(
                    world,
                    catalog,
                    npc,
                    world.Pilots.Find(f.PilotId)!,
                    f.ShipId,
                    new NpcProfileCatalog().Read(catalog, npc.TypeId).Attributes,
                    new(1e9, 1e9, 1e9, 1e9),
                    f.Clock.Now
                );
                Assert.True(outcome.Destroyed);
                reward += outcome.Reward!.AmountCents;
                killed++;
            }
            world.SaveChanges();
            transaction.Commit();
        }
        Assert.Empty(sites.Signatures(site.SystemId, f.Clock.Now));
        Assert.False(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        (scanning, sites) = Stores(f);
        Assert.Empty(sites.Signatures(site.SystemId, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(killed, world.Set<WorldWreck>().Count());
            Assert.Equal(reward, world.Set<NpcBounty>().Sum(b => b.AmountCents));
        }
        long due = f.Clock.Now + CombatAnomalyRules.ReplacementSeconds * TimeSpan.TicksPerSecond;
        Assert.Empty(sites.Signatures(site.SystemId, due - 1));
        var replacement = Assert.Single(sites.Signatures(site.SystemId, due));
        Assert.NotEqual(site.Id, replacement.Id);
        Assert.NotEqual(site.TargetId, replacement.TargetId);
        Assert.False(scanning.Resolved(f.PilotId, replacement.Id, replacement.SystemId, due));
    }

    [Fact]
    public void StationInferredSystemPreservesProbeLimitsRecoveryDestructionAndPendingScansAfterRestart()
    {
        using var f = Prepare();
        long module = Load(f, explicitSystem: false);
        var (scanning, sites) = Stores(f);
        int system = f.Db.Store.FindPilot(1000001)!.SystemId;
        var first = scanning.Launch(f.PilotId, module, 4, f.Clock.Now);
        f.Clock.Milliseconds += 2000;
        var second = scanning.Launch(f.PilotId, module, 8, f.Clock.Now);
        Assert.Equal(4, second.Probes.Count);
        var probes = first.Probes.Concat(second.Probes).ToArray();
        Assert.All(probes, p => Assert.Equal(system, p.SystemId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Null(world.Positions.Find(f.ShipId)!.SystemId);
        }
        long spare = f.Grant(30013, 8, cargo: true);
        f.Clock.Milliseconds += 2000;
        f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, f.ShipId, [module], [spare], f.ShipId, f.Clock.Now);
        f.Clock.Milliseconds += 10000;
        Assert.Throws<InvalidDataException>(() => scanning.Launch(f.PilotId, module, 1, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(8, world.Set<OwnedItem>().Single(i => i.LocationId == module).Quantity);
            Assert.Equal(8, world.Set<SavedScanProbe>().Count());
        }
        var site = Assert.Single(sites.Signatures(system, f.Clock.Now));
        var scan = scanning.Begin(f.PilotId, Formation(probes, site.Position), f.Clock.Now);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        scanning = Stores(f).Scanning;
        Assert.Equal(scan.ReadyAt, scanning.Pending(f.PilotId)!.ReadyAt);
        f.Clock.Milliseconds += (scan.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        Assert.Equal(1, Assert.Single(scanning.Complete(f.PilotId, scan, f.Clock.Now)).Certainty);
        Assert.True(scanning.Resolved(f.PilotId, site.Id, system, f.Clock.Now));
        var recovered = scanning.Recover(f.PilotId, first.Probes.Select(p => p.Id).ToArray(), f.Clock.Now);
        Assert.Equal(4, Assert.Single(recovered.Items).Item.Quantity);
        foreach (var probe in second.Probes)
        {
            scanning.Destroy(f.PilotId, probe.Id, f.Clock.Now);
        }
        Assert.Empty(scanning.List(f.PilotId, system, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => scanning.Destroy(f.PilotId, second.Probes[0].Id, f.Clock.Now));
    }

    [Fact]
    public void DockingRejectsProbeCommandsAndDiscardsPendingResultsWithoutChangingInventoryOrProbes()
    {
        using var f = Prepare();
        long module = Load(f, explicitSystem: false);
        var (scanning, sites) = Stores(f);
        int system = f.Db.Store.FindPilot(1000001)!.SystemId;
        var probes = scanning.Launch(f.PilotId, module, 8, f.Clock.Now).Probes;
        var site = Assert.Single(sites.Signatures(system, f.Clock.Now));
        var placements = Formation(probes, site.Position);
        var scan = scanning.Begin(f.PilotId, placements, f.Clock.Now);
        var deployed = scanning.List(f.PilotId, system, f.Clock.Now);
        f.Db.Store.SetPosition(f.ShipId, null);
        f.Clock.Milliseconds += (scan.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        Assert.Throws<InvalidDataException>(() => scanning.Launch(f.PilotId, module, 1, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => scanning.Recover(f.PilotId, probes.Select(p => p.Id).ToArray(), f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => scanning.Destroy(f.PilotId, probes[0].Id, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => scanning.Begin(f.PilotId, placements, f.Clock.Now));
        Assert.Empty(scanning.Complete(f.PilotId, scan, f.Clock.Now));
        Assert.False(scanning.Resolved(f.PilotId, site.Id, system, f.Clock.Now));
        Assert.Equal(deployed, scanning.List(f.PilotId, system, f.Clock.Now));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == 30013));
        Assert.Empty(world.Set<SavedProbeScan>());
    }

    [Fact]
    public void PublishedLaunchersAndProbesHaveFittingAndChargeAdmission()
    {
        using var f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var launchers = catalog.Set<Eden.Data.Catalog.ItemType>().Where(t => t.GroupId == ProbeScanRules.LauncherGroup).ToArray();
        Assert.Equal(7, launchers.Length);
        foreach (var type in launchers)
        {
            Assert.True(ProbeCatalog.Launcher(catalog, type.Id, type.GroupId), type.Name);
            Assert.NotNull(FittingCatalog.Definition(catalog, type.Id));
        }
        var probes = catalog.Set<Eden.Data.Catalog.ItemType>().Where(t => t.GroupId == ProbeScanRules.ProbeGroup).ToArray();
        Assert.Equal(6, probes.Length);
        Assert.All(probes, p => Assert.True(ProbeCatalog.Probe(catalog, p.Id, p.GroupId), p.Name));
    }

    [Fact]
    public void RecoveryConservesInventoryRejectsReplayAndKeepsProbeIdsUniqueAfterReopen()
    {
        using var f = Prepare();
        long module = Load(f, quantity: 16);
        var (scanning, _) = Stores(f);
        var first = scanning.Launch(f.PilotId, module, 8, f.Clock.Now);
        var ids = first.Probes.Select(p => p.Id).ToArray();
        Assert.Equal(0, first.Magazine.Quantity);
        Assert.Throws<InvalidDataException>(() => scanning.Launch(f.PilotId, module, 1, f.Clock.Now));
        var recovered = scanning.Recover(f.PilotId, ids, f.Clock.Now);
        Assert.Equal(8, Assert.Single(recovered.Items).Item.Quantity);
        Assert.Empty(scanning.List(f.PilotId, CombatAnomalyRules.SystemId, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => scanning.Recover(f.PilotId, ids, f.Clock.Now));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        scanning = Stores(f).Scanning;
        f.Clock.Milliseconds += 2000;
        f.Db.Services.GetRequiredService<IPilotAmmunition>()
            .Load(f.PilotId, f.ShipId, [module], [Assert.Single(recovered.Items).Item.Id], f.ShipId, f.Clock.Now);
        f.Clock.Milliseconds += 10000;
        var second = scanning.Launch(f.PilotId, module, 8, f.Clock.Now);
        Assert.All(second.Probes, p => Assert.True(p.Id > ids.Max()));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(8, world.Set<OwnedItem>().Where(i => i.OwnerId == f.PilotId && i.TypeId == 30013).Sum(i => i.Quantity));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("range")]
    [InlineData("nonfinite")]
    public void InvalidScanLeavesProbesAndResolutionUnchanged(string reason)
    {
        using var f = Prepare();
        long module = Load(f);
        var (scanning, sites) = Stores(f);
        var launched = scanning.Launch(f.PilotId, module, 8, f.Clock.Now);
        var site = Assert.Single(sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now));
        var positions = Formation(launched.Probes, site.Position);
        positions[0] = reason switch
        {
            "foreign" => positions[0] with { Id = 123 },
            "duplicate" => positions[0] with { Id = positions[1].Id },
            "range" => positions[0] with { RangeStep = 9 },
            _ => positions[0] with { Destination = new(double.NaN, 0, 0) },
        };
        Assert.Throws<InvalidDataException>(() => scanning.Begin(f.PilotId, positions, f.Clock.Now));
        Assert.Equal(launched.Probes, scanning.List(f.PilotId, site.SystemId, f.Clock.Now));
        Assert.Null(scanning.Pending(f.PilotId));
        Assert.False(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeLaunchScanNotificationsAndWarpRequireAPerPilotPerfectResult(bool explicitSystem)
    {
        using var f = Prepare();
        long module = Load(f, explicitSystem: explicitSystem);
        var (scanning, sites) = Stores(f);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), []),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var site = Assert.Single(sites.Signatures(pilot.SystemId, f.Clock.Now));
        using var scope = f.Db.Services.CreateScope();
        var flight = scope.ServiceProvider.GetRequiredService<FlightCallHandler>();
        Dictionary<string, object?> warp = new() { ["minRange"] = 0d, ["fleet"] = false };
        Assert.IsType<PilotCallResult.Failure>(flight.Call(context, "beyonce", "CmdWarpToStuff", ["scan", site.TargetId], warp));
        var handler = scope.ServiceProvider.GetRequiredService<ProbeScanningHandler>();
        var tracker = scope.ServiceProvider.GetRequiredService<CombatAnomalyHandler>();
        Assert.IsType<PilotCallResult.Success>(tracker.Call(context, "scanMgr", "SignalTrackerRegister", []));
        var full = Assert.Single(context.Notifications.OfType<SignalTrackerFullState>());
        Assert.Equal(site.Id, Assert.Single(full.Signatures!).Id);
        var hidden = Assert.IsType<Dictionary<long, object?>>(new SignatureMap(full.Signatures!).ToBlue());
        var info = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(hidden[site.Id]).State);
        Assert.Null(info["dungeonID"]);
        Assert.Null(info["archetypeID"]);
        Assert.NotEqual(site.Position.X, ((object[])info["position"]!)[0]);
        Assert.IsType<PilotCallResult.Success>(
            scope
                .ServiceProvider.GetRequiredService<Eden.Protocol.Docking.ModuleActivationHandler>()
                .Call(context, "dogmaIM", "Activate", [module, "useMissiles", null, 0L])
        );
        Assert.Equal(8, context.Notifications.OfType<NewScanProbe>().Count());
        var probes = scanning.List(f.PilotId, pilot.SystemId, f.Clock.Now);
        var request = Formation(probes, site.Position)
            .ToDictionary(
                p => p.Id,
                p =>
                    (object?)
                        new BlueInstance(
                            "util.KeyVal",
                            new Dictionary<string, object?>
                            {
                                ["probeID"] = p.Id,
                                ["destination"] = ProbeScanningWire.Position(p.Destination),
                                ["rangeStep"] = (long)p.RangeStep,
                                ["scanRange"] = double.PositiveInfinity,
                                ["typeID"] = 123L,
                            }
                        )
            );
        var decoded = Assert.IsType<object[]>(BlueMarshal.Decode(BlueMarshal.Encode(new object[] { request })));
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "@systemScan", "RequestScans", decoded));
        var started = Assert.Single(context.Notifications.OfType<SystemScanStarted>());
        Assert.Equal(3, started.Arguments.Length);
        handler.Advance(context);
        Assert.Empty(context.Notifications.OfType<SystemScanStopped>());
        f.Clock.Milliseconds += (started.Scan.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        handler.Advance(context);
        var stopped = Assert.Single(context.Notifications.OfType<SystemScanStopped>());
        Assert.Equal(1, Assert.Single(stopped.Results).Certainty);
        Assert.Equal(3, stopped.Arguments.Length);
        _ = BlueMarshal.Decode(BlueMarshal.Encode(stopped.Arguments));
        Assert.True(scanning.Resolved(f.PilotId, site.Id, pilot.SystemId, f.Clock.Now));
        Assert.False(scanning.Resolved(f.Db.Store.FindPilot(1000002)!.PilotId, site.Id, pilot.SystemId, f.Clock.Now));
        Assert.IsType<PilotCallResult.Success>(flight.Call(context, "beyonce", "CmdWarpToStuff", ["scan", site.TargetId], warp));
    }

    [Fact]
    public void ScanningSkillsUpdateDeployedProbeStrengthDeviationAndDurationOnTheNextScan()
    {
        using var f = Prepare();
        long module = Load(f);
        f.Train(ProbeScanRules.AstrometricsSkill, 1);
        var (scanning, sites) = Stores(f);
        var launch = scanning.Launch(f.PilotId, module, 8, f.Clock.Now);
        Assert.Equal(42, launch.Probes[0].Profile.Strength, 8);
        Assert.Equal(0.11875, launch.Probes[0].Profile.DeviationAu, 8);
        var site = Assert.Single(sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now));
        var first = scanning.Begin(f.PilotId, Formation(launch.Probes, site.Position), f.Clock.Now);
        Assert.Equal(9500, (first.ReadyAt - first.StartedAt) / (double)TimeSpan.TicksPerMillisecond, 8);
        f.Clock.Milliseconds += 9500;
        scanning.Complete(f.PilotId, first, f.Clock.Now);
        f.Train(ProbeScanRules.AstrometricsSkill, 5);
        f.Train(25739, 5);
        f.Train(25810, 5);
        f.Train(25811, 5);
        var improved = scanning.Begin(f.PilotId, Formation(launch.Probes, site.Position), f.Clock.Now);
        Assert.Equal(62.5, improved.Probes[0].Profile.Strength, 8);
        Assert.Equal(0.0703125, improved.Probes[0].Profile.DeviationAu, 8);
        Assert.Equal(5625, (improved.ReadyAt - improved.StartedAt) / (double)TimeSpan.TicksPerMillisecond, 8);
    }

    [Fact]
    public void ExplorationHullAndScannerEquipmentBonusesFeedActualDeployedProbes()
    {
        using var f = Prepare();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.ShipId)!.TypeId = 605;
            world.SaveChanges();
        }
        f.Train(3330, 5);
        f.Fit(33180);
        f.Fit(33178);
        f.Fit(33176);
        long module = Load(f);
        var (scanning, sites) = Stores(f);
        var launch = scanning.Launch(f.PilotId, module, 8, f.Clock.Now);
        Assert.True(launch.Probes[0].Profile.Strength > 68.75);
        Assert.Equal(37.5, launch.Probes[0].Profile.Bonuses!.ShipStrength, 8);
        Assert.True(launch.Probes[0].Profile.Bonuses!.ModuleStrength > 0);
        Assert.True(launch.Probes[0].Profile.Bonuses!.ModuleDeviation < 0);
        Assert.True(launch.Probes[0].Profile.DeviationAu < 0.09375);
        var site = Assert.Single(sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now));
        var scan = scanning.Begin(f.PilotId, Formation(launch.Probes, site.Position), f.Clock.Now);
        Assert.True((scan.ReadyAt - scan.StartedAt) / (double)TimeSpan.TicksPerMillisecond < 7500);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("cargo")]
    [InlineData("expired")]
    [InlineData("scanning")]
    public void FailedRecoveryDoesNotCreateCargoOrRemoveOwnedProbes(string reason)
    {
        using var f = Prepare();
        long module = Load(f);
        var (scanning, sites) = Stores(f);
        var launch = scanning.Launch(f.PilotId, module, 8, f.Clock.Now);
        var ids = launch.Probes.Select(p => p.Id).ToArray();
        long pilot = f.PilotId;
        long now = f.Clock.Now;
        if (reason == "foreign")
        {
            pilot = f.Db.Store.FindPilot(1000002)!.PilotId;
        }
        if (reason == "cargo")
        {
            f.Grant(30013, 100000, cargo: true);
        }
        if (reason == "expired")
        {
            now = launch.Probes[0].ExpiresAt;
        }
        if (reason == "scanning")
        {
            var site = Assert.Single(sites.Signatures(CombatAnomalyRules.SystemId, now));
            scanning.Begin(pilot, Formation(launch.Probes, site.Position), now);
        }
        int quantity;
        using (WorldDbContext world = new(f.Db.Path))
        {
            quantity = world.Set<OwnedItem>().Where(i => i.TypeId == 30013).Sum(i => i.Quantity);
        }
        if (reason == "cargo")
        {
            Assert.Throws<CargoCapacityException>(() => scanning.Recover(pilot, ids, now));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => scanning.Recover(pilot, ids, now));
        }
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(8, verify.Set<SavedScanProbe>().Count());
        Assert.Equal(quantity, verify.Set<OwnedItem>().Where(i => i.TypeId == 30013).Sum(i => i.Quantity));
    }

    [Theory]
    [InlineData(605)]
    [InlineData(17938)]
    [InlineData(30013)]
    public void BasicExplorationEquipmentCanBeManufacturedAndUnlockRegionalSupply(int product)
    {
        using var f = Prepare();
        f.Train((int)IndustrySkill.Industry, 5);
        int blueprintType;
        int quantity;
        double seconds;
        Dictionary<int, int> materials;
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{\"services\":[5,7,14,17]}}')"
            );
        }
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            blueprintType = catalog.Definitions.BlueprintsForProduct(product).Single();
            var recipe = catalog.Definitions.ManufacturingRecipe(blueprintType)!;
            quantity = (int)Assert.Single(recipe.Products ?? []).Quantity!.Value;
            materials = (recipe.Materials ?? []).ToDictionary(m => (int)m.TypeID!.Value, m => (int)m.Quantity!.Value);
            seconds = Math.Round(recipe.Time!.Value * 0.8, MidpointRounding.ToEven);
            foreach (var skill in recipe.Skills ?? [])
            {
                f.Train((int)skill.TypeID!.Value, 5);
            }
        }
        long blueprintId = f.Grant(blueprintType);
        foreach (var (type, amount) in materials)
        {
            f.Grant(type, amount);
        }
        PilotIndustryStore industry = new(new(f.Db.Path), f.Clock, new IndustryRandom());
        Assert.Contains(product, industry.ProductTypes());
        var installed = industry.Install(f.PilotId, new(blueprintId, blueprintType, (int)f.StationId, 1, product, seconds, materials));
        f.Clock.Milliseconds += installed.Job.DurationSeconds * 1000;
        var delivered = industry.Deliver(f.PilotId, installed.Job.Id, installed.Job.Facility.SystemId);
        Assert.Equal(quantity, Assert.Single(delivered.Changes).Item.Quantity);
        Assert.Empty(industry.Deliver(f.PilotId, installed.Job.Id, installed.Job.Facility.SystemId).Changes);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.NotNull(saved.Set<Eden.Data.World.Market.SavedMarketUnlock>().Find(product));
    }

    [Fact]
    public void DetachedCheckpointKeepsProbesAndPendingScanFromTheSameGeneration()
    {
        using var f = Prepare();
        long module = Load(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var (scanning, sites) = Stores(f);
        var site = Assert.Single(sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now));
        var probes = scanning.Launch(f.PilotId, module, 8, f.Clock.Now).Probes;
        var scan = scanning.Begin(f.PilotId, Formation(probes, site.Position), f.Clock.Now);
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        f.Clock.Milliseconds += (scan.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        Assert.Single(scanning.Complete(f.PilotId, scan, f.Clock.Now));
        scanning.Recover(f.PilotId, probes.Select(p => p.Id).ToArray(), f.Clock.Now);
        snapshot.Project();
        using WorldDbContext captured = new(snapshot.Database);
        Assert.Equal(8, captured.Set<SavedScanProbe>().Count());
        Assert.Equal(scan.ReadyAt, captured.Set<SavedProbeScan>().Single().ReadyAt);
        Assert.Empty(captured.Set<SavedResolvedSignature>());
        Assert.Empty(scanning.List(f.PilotId, site.SystemId, f.Clock.Now));
        Assert.Null(scanning.Pending(f.PilotId));
        Assert.True(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
    }

    [Fact]
    public void LeavingTheSystemBeforeCompletionPreventsDisclosureAndResolution()
    {
        using var f = Prepare();
        long module = Load(f);
        var (scanning, sites) = Stores(f);
        var site = Assert.Single(sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now));
        var probes = scanning.Launch(f.PilotId, module, 8, f.Clock.Now).Probes;
        var scan = scanning.Begin(f.PilotId, Formation(probes, site.Position), f.Clock.Now);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero, site.SystemId + 1);
        f.Clock.Milliseconds += (scan.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        Assert.Empty(scanning.Complete(f.PilotId, scan, f.Clock.Now));
        Assert.False(scanning.Resolved(f.PilotId, site.Id, site.SystemId, f.Clock.Now));
        Assert.Equal(8, scanning.List(f.PilotId, site.SystemId, f.Clock.Now).Count);
    }

    [Fact]
    public void WarmProbeMonitoringDoesNotReadSqlOrRecalculateFittings()
    {
        using var f = Prepare();
        long module = Load(f);
        var (scanning, _) = Stores(f);
        scanning.Launch(f.PilotId, module, 8, f.Clock.Now);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), []),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        ProbeScanningHandler handler = new(scanning, f.Clock);
        handler.Advance(context);
        context.Notifications.Clear();
        using var metrics = SessionPerformance.Begin("probe monitoring", _ => { }, force: true)!;
        for (int i = 0; i < 20; i++)
        {
            f.Clock.Milliseconds += 1000;
            handler.Advance(context);
        }
        Assert.Empty(context.Notifications);
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
