using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class HackingWorldTests
{
    private static IReadOnlyList<HackingCoordinate>? PathToCore(HackingBoard board)
    {
        Queue<HackingCoordinate> pending = new();
        Dictionary<HackingCoordinate, HackingCoordinate?> visited = new() { [new(0, 0)] = null };
        pending.Enqueue(new(0, 0));
        while (pending.TryDequeue(out var current))
        {
            var tile = board.Tiles.Single(t => t.Coordinate == current);
            if (tile.Type == HackingNode.Core)
            {
                List<HackingCoordinate> path = [];
                HackingCoordinate? step = current;
                while (step is not null)
                {
                    path.Add(step);
                    step = visited[step];
                }
                path.Reverse();
                return path;
            }
            foreach (
                var next in board.Tiles.Where(t =>
                    t.Type is HackingNode.Segment or HackingNode.Core
                    && !visited.ContainsKey(t.Coordinate)
                    && HackingRules.Neighbors(current, t.Coordinate)
                )
            )
            {
                visited[next.Coordinate] = current;
                pending.Enqueue(next.Coordinate);
            }
        }
        return null;
    }

    private static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadHacking(f.CatalogPath);
        return f;
    }

    private static (PirateExplorationStore Store, CombatAnomaly Site, long Module, StaticSpaceObject Container) Start(
        Fixture f,
        ExplorationKind kind = ExplorationKind.Data
    )
    {
        long module = f.Fit(kind == ExplorationKind.Data ? 22175 : 22177);
        PirateExplorationStore store = new(new(f.Db.Path));
        var site = store.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now).Single(s => s.Exploration == kind);
        var container = site.Objects[0];
        f.Db.Store.SetPosition(f.ShipId, container.Position, site.SystemId);
        return (store, site, module, container);
    }

    private static HackingBoard WinningBoard() =>
        new(
            [
                new(2, new(0, 0), HackingNode.Segment, HackingSubsystem.None, Hidden: false),
                new(3, new(1, 0), HackingNode.Core, HackingSubsystem.LowCore, 20, 10, false),
            ],
            [],
            90,
            25,
            25
        );

    private static void Finish(PirateExplorationStore store, Fixture f)
    {
        using (WorldDbContext world = new(f.Db.Path))
        {
            var attempt = world.Set<SavedHackingAttempt>().Find(f.PilotId)!;
            var contact = JsonSerializer.Deserialize<HackingContact>(attempt.ContactJson)!;
            attempt.ContactJson = JsonSerializer.Serialize(contact with { Board = WinningBoard() });
            world.SaveChanges();
        }
        var turn = store.Act(f.PilotId, new(HackingActionKind.Click, new(1, 0)), f.Clock.Now);
        Assert.Equal(HackingOutcome.Won, turn.Current.Outcome);
    }

    [Theory]
    [InlineData(ExplorationKind.Data)]
    [InlineData(ExplorationKind.Relic)]
    public void AnalyzerPaysUsesSkillsUnlocksRealLootAndCannotRewardTwiceAcrossRestart(ExplorationKind kind)
    {
        using var f = Prepare();
        var (store, site, module, container) = Start(f, kind);
        Assert.Throws<InvalidDataException>(() => store.Read(f.PilotId, container.Id, f.Clock.Now));
        var contact = store.Begin(f.PilotId, module, container.Id, new HashSet<long> { container.Id }, f.Clock.Now);
        Assert.Equal(90, contact.Module.Coherence);
        Assert.Equal(20, contact.Module.Strength);
        Assert.Equal(3, contact.Module.Slots);
        Assert.Equal(10000, contact.Module.DurationMs);
        Assert.Equal(
            f.Fitting.ShipAttributes(f.PilotId)[Eden.Gameplay.Dogma.DogmaAttributeId.CapacitorCapacity] - 20,
            contact.Capacitor,
            6
        );
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(contact.ReadyAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
            Assert.Empty(world.Set<OwnedItem>().Where(i => i.LocationId == container.Id));
        }
        Finish(store, f);
        var loot = store.Read(f.PilotId, container.Id, f.Clock.Now);
        Assert.NotEmpty(loot.Items);
        Assert.All(
            loot.Items,
            item =>
                Assert.True(
                    kind == ExplorationKind.Relic
                        ? item.GroupId == HackingCatalog.SalvageGroup
                        : item.GroupId is HackingCatalog.DatacoreGroup or HackingCatalog.DecryptorGroup
                )
        );
        int total = loot.Items.Sum(i => i.Quantity);
        Assert.InRange(total, 2, 6);
        Assert.Throws<InvalidDataException>(() => store.Act(f.PilotId, new(HackingActionKind.Click, new(1, 0)), f.Clock.Now));
        Assert.Throws<InvalidDataException>(() =>
            store.Begin(f.PilotId, module, container.Id, new HashSet<long> { container.Id }, contact.ReadyAt)
        );
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        store = new(new(f.Db.Path));
        var reopened = store.Read(f.PilotId, container.Id, f.Clock.Now);
        Assert.Equal(total, reopened.Items.Sum(i => i.Quantity));
        var request = new InventoryMoveRequest(reopened.Items.Select(i => i.Id).ToArray(), container.Id, (int)InventoryFlag.Cargo, null);
        var changes = store.Take(f.PilotId, f.ShipId, request, f.Clock.Now);
        Assert.Equal(total, changes.Sum(c => c.Item.Quantity));
        Assert.Empty(store.Read(f.PilotId, container.Id, f.Clock.Now).Items);
        Assert.Throws<InvalidDataException>(() => store.Take(f.PilotId, f.ShipId, request, f.Clock.Now));
        Assert.Equal(site.TargetId, store.Signatures(site.SystemId, f.Clock.Now).Single(s => s.Id == site.Id).TargetId);
    }

    [Fact]
    public void RestartAndRepeatedQuitCountEachAbandonedAttemptOnceAndPreservePaidCooldown()
    {
        using var f = Prepare();
        var (store, site, module, container) = Start(f);
        var contact = store.Begin(f.PilotId, module, container.Id, new HashSet<long> { container.Id }, f.Clock.Now);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(1, world.Set<SavedHackingContainer>().Find(container.Id)!.Failures);
            Assert.Empty(world.Set<SavedHackingAttempt>());
            Assert.Equal(contact.ReadyAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        }
        store = new(new(f.Db.Path));
        store.Begin(f.PilotId, module, container.Id, new HashSet<long> { container.Id }, contact.ReadyAt);
        store.Cancel(f.PilotId, contact.ReadyAt);
        store.Cancel(f.PilotId, contact.ReadyAt);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(2, verify.Set<SavedHackingContainer>().Find(container.Id)!.Failures);
        Assert.True(verify.Set<SavedHackingContainer>().Find(container.Id)!.Destroyed);
        Assert.DoesNotContain(store.Signatures(site.SystemId, contact.ReadyAt).SelectMany(s => s.Objects), c => c.Id == container.Id);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("range")]
    [InlineData("module")]
    [InlineData("wrongKind")]
    public void RejectedActivationDoesNotPayOrReserveContainer(string reason)
    {
        using var f = Prepare();
        var (store, site, module, container) = Start(f);
        if (reason == "range")
        {
            f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero, site.SystemId);
        }
        if (reason == "module")
        {
            module++;
        }
        if (reason == "wrongKind")
        {
            container = store.Signatures(site.SystemId, f.Clock.Now).Single(s => s.Exploration == ExplorationKind.Relic).Objects[0];
            f.Db.Store.SetPosition(f.ShipId, container.Position, site.SystemId);
        }
        Assert.Throws<InvalidDataException>(() =>
            store.Begin(
                f.PilotId,
                module,
                container.Id,
                reason == "lock" ? new HashSet<long>() : new HashSet<long> { container.Id },
                f.Clock.Now
            )
        );
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Empty(verify.Set<SavedHackingAttempt>());
        Assert.Null(verify.Set<ModuleCooldown>().Find(module));
        Assert.False(verify.Set<SavedHackingContainer>().Find(container.Id)!.Hacked);
    }

    [Fact]
    public void CatalogAdmitsPublishedAnalyzersAndWarmSiteReadsUseNoSqlOrFittingCalculations()
    {
        using var f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog
            .Records(CatalogData.Client.Types)
            .Where(p => p.Value.GroupID == HackingRules.AnalyzerGroup && p.Value.Published == 1 && p.Value.MarketGroupID is not null)
            .ToArray();
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            Assert.True(HackingCatalog.Supported(catalog, (int)type.Key, HackingRules.AnalyzerGroup), type.Key.ToString());
            Assert.NotNull(FittingCatalog.Definition(catalog, (int)type.Key));
        }
        PirateExplorationStore store = new(new(f.Db.Path));
        var first = store.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now);
        Assert.Equal(2, first.Count);
        using var metrics = SessionPerformance.Begin("warm exploration", _ => { }, force: true)!;
        for (int i = 0; i < 20; i++)
        {
            Assert.Same(first, store.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now + i));
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Theory]
    [InlineData(ExplorationKind.Data)]
    [InlineData(ExplorationKind.Relic)]
    public void NativeTargetAnalyzerGeneratedBoardAndInventoryCallsCompleteTheExplorationLoop(ExplorationKind kind)
    {
        using var f = Prepare();
        var (store, site, module, container) = Start(f, kind);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var row = world.Set<SavedHackingContainer>().Find(container.Id)!;
            int difficulty = HackingCatalog.Sites.Single(s => s.DungeonId == site.DungeonId).Containers[row.Index].Difficulty;
            row.LootSeed = Enumerable
                .Range(0, 1000)
                .First(seed => PathToCore(HackingRules.Create(seed, difficulty, 90, 20, 3)) is not null);
            world.SaveChanges();
        }
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), site.Objects),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        using var scope = f.Db.Services.CreateScope();
        var targets = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        Assert.NotNull(context.Flight.FindTarget(container.Id));
        Assert.IsType<PilotCallResult.Success>(targets.Call(context, "dogmaIM", "AddTarget", [container.Id]));
        f.Clock.Milliseconds += 60000;
        targets.Advance(context);
        Assert.True(targets.IsLocked(container.Id));
        var handler = scope.ServiceProvider.GetRequiredService<HackingHandler>();
        Assert.IsType<PilotCallResult.Failure>(handler.Call(context, "hackingMgr", "StartNewGameInstance", []));
        Assert.IsType<PilotCallResult.Success>(
            scope
                .ServiceProvider.GetRequiredService<ModuleActivationHandler>()
                .Call(context, "dogmaIM", "Activate", [module, "doHacking", container.Id, 0L])
        );
        var start = Assert.Single(context.Notifications.OfType<HackingTurnCompleted>());
        Assert.Equal(kind, start.Contact.Kind);
        using (var activeMetrics = SessionPerformance.Begin("active hacking", _ => { }, force: true)!)
        {
            for (int i = 0; i < 20; i++)
            {
                handler.Advance(context);
            }
            Assert.Equal(0, activeMetrics.Commands);
            Assert.Equal(0, activeMetrics.Connections);
            Assert.Equal(0, activeMetrics.FittingCalculations);
        }
        var path = PathToCore(start.Board)!;
        foreach (var point in path.Skip(1))
        {
            Assert.IsType<PilotCallResult.Success>(
                handler.Call(context, "hackingMgr", "ClickedOnTile", [new object[] { (long)point.X, (long)point.Y }])
            );
        }
        for (int shot = 0; shot < 10 && handler.IsMonitoring; shot++)
        {
            var core = path[^1];
            Assert.IsType<PilotCallResult.Success>(
                handler.Call(context, "hackingMgr", "ClickedOnTile", [new object[] { (long)core.X, (long)core.Y }])
            );
        }
        Assert.False(handler.IsMonitoring);
        Assert.Equal(HackingOutcome.Won, context.Notifications.OfType<HackingTurnCompleted>().Last().Board.Outcome);
        foreach (var notification in context.Notifications.OfType<HackingTurnCompleted>())
        {
            Assert.IsType<object[]>(BlueMarshal.Decode(BlueMarshal.Encode(notification.Arguments)));
        }
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "invbroker", "GetInventoryFromId", [container.Id]));
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "@explorationcontainer:" + container.Id, "List", []));
        var contents = store.Read(f.PilotId, container.Id, f.Clock.Now).Items;
        Assert.NotEmpty(contents);
        Assert.IsType<PilotCallResult.Success>(
            scope
                .ServiceProvider.GetRequiredService<InventoryTransfers>()
                .Move(context, "@ship", new(contents.Select(i => i.Id).ToArray(), container.Id, (int)InventoryFlag.Cargo, null))
        );
        Assert.Empty(store.Read(f.PilotId, container.Id, f.Clock.Now).Items);
        using var metrics = SessionPerformance.Begin("idle hacking", _ => { }, force: true)!;
        for (int i = 0; i < 20; i++)
        {
            handler.Advance(context);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void ProbeScanResolvesDataAndRelicSignaturesAndRejectsExpiredWarpRights()
    {
        using var f = Prepare();
        PassiveCatalogFixture.LoadProbes(f.CatalogPath);
        f.Train(ProbeScanRules.ScienceSkill, 5);
        var (store, site, _, container) = Start(f);
        f.Db.Store.SetPosition(f.ShipId, null);
        long launcher = f.Fit(17938);
        long charge = f.Grant(30013, 8);
        f.Db.Services.GetRequiredService<Eden.Gameplay.Ammunition.IPilotAmmunition>()
            .Load(f.PilotId, f.ShipId, [launcher], [charge], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 10000;
        f.Db.Store.SetPosition(f.ShipId, container.Position, site.SystemId);
        CombatAnomalyStore signatures = new(new(f.Db.Path), store);
        PilotProbeScanningStore scanner = new(new(f.Db.Path), signatures);
        var probes = scanner.Launch(f.PilotId, launcher, 8, f.Clock.Now).Probes;
        foreach (var target in store.Signatures(site.SystemId, f.Clock.Now))
        {
            var placements = ProbeScanningRuleTests
                .Formation(target.Position)
                .Select((p, i) => p.Placement with { Id = probes[i].Id })
                .ToArray();
            var scan = scanner.Begin(f.PilotId, placements, f.Clock.Now);
            f.Clock.Milliseconds += (scan.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
            var result = Assert.Single(scanner.Complete(f.PilotId, scan, f.Clock.Now), r => r.Site.Id == target.Id);
            Assert.Equal(1, result.Certainty);
            Assert.True(scanner.Resolved(f.PilotId, target.Id, site.SystemId, f.Clock.Now));
            var wire = ProbeScanningWire.Result(result);
            var state = Assert.IsType<Dictionary<string, object?>>(wire.State);
            Assert.Equal(
                target.Exploration == ExplorationKind.Data ? HackingRules.DataArchetype : HackingRules.RelicArchetype,
                state["archetypeID"]
            );
            Assert.False(scanner.Resolved(f.PilotId, target.Id, site.SystemId, f.Clock.Now + HackingRules.SiteLifetime));
        }
    }

    [Fact]
    public void CargoRejectionAndAnotherPilotsReadLeaveEarnedLootUnchanged()
    {
        using var f = Prepare();
        var (store, site, module, container) = Start(f);
        store.Begin(f.PilotId, module, container.Id, new HashSet<long> { container.Id }, f.Clock.Now);
        Finish(store, f);
        var loot = store.Read(f.PilotId, container.Id, f.Clock.Now);
        var other = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(other.ShipId, container.Position, site.SystemId);
        Assert.Throws<InvalidDataException>(() => store.Read(other.PilotId, container.Id, f.Clock.Now));
        f.Grant(34, 1_000_000, cargo: true);
        var request = new InventoryMoveRequest(loot.Items.Select(i => i.Id).ToArray(), container.Id, (int)InventoryFlag.Cargo, null);
        Assert.Throws<CargoCapacityException>(() => store.Take(f.PilotId, f.ShipId, request, f.Clock.Now));
        Assert.Equal(loot.Items, store.Read(f.PilotId, container.Id, f.Clock.Now).Items);
        Assert.Throws<InvalidDataException>(() => store.Take(f.PilotId, f.ShipId + 1, request, f.Clock.Now));
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero, site.SystemId + 1);
        Assert.Throws<InvalidDataException>(() => store.Take(f.PilotId, f.ShipId, request, f.Clock.Now));
    }

    [Fact]
    public void CompletingEveryContainerRetiresSiteAndReplacementHasNewIdentityAcrossRestart()
    {
        using var f = Prepare();
        var (store, site, _, _) = Start(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            foreach (var row in world.Set<SavedHackingContainer>().Where(c => c.SiteId == site.Id))
            {
                row.Failures = 1;
            }
            world.SaveChanges();
        }
        long next = f.Clock.Now;
        foreach (var container in site.Objects)
        {
            f.Db.Store.SetPosition(f.ShipId, container.Position, site.SystemId);
            long module;
            using (WorldDbContext world = new(f.Db.Path))
            {
                module = world.Set<OwnedItem>().Single(i => i.OwnerId == f.PilotId && i.TypeId == 22175 && i.Online).Id;
            }
            var contact = store.Begin(f.PilotId, module, container.Id, new HashSet<long> { container.Id }, next);
            store.Cancel(f.PilotId, next);
            next = contact.ReadyAt;
        }
        Assert.DoesNotContain(store.Signatures(site.SystemId, next), s => s.Id == site.Id);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        store = new(new(f.Db.Path));
        Assert.DoesNotContain(store.Signatures(site.SystemId, next), s => s.Id == site.Id);
        var replacement = store
            .Signatures(site.SystemId, next + HackingRules.ReplacementDelay)
            .Single(s => s.Exploration == site.Exploration);
        Assert.NotEqual(site.Id, replacement.Id);
        Assert.NotEqual(site.TargetId, replacement.TargetId);
        Assert.DoesNotContain(replacement.Objects, c => site.Objects.Any(old => old.Id == c.Id));
    }

    [Fact]
    public void EarnedHullAndRigBonusesReachTheAnalyzerProfile()
    {
        using var f = Prepare();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.ShipId)!.TypeId = 605;
            world.SaveChanges();
        }
        f.Train(3330, 5);
        f.Fit(31238, online: false);
        var (store, _, module, container) = Start(f);
        var profile = store.Begin(f.PilotId, module, container.Id, new HashSet<long> { container.Id }, f.Clock.Now).Module;
        Assert.Equal(25, profile.Strength);
        Assert.True(profile.Coherence > 90);
    }

    [Fact]
    public void DetachedCheckpointRetainsTheBoardAndContainerFromOneGeneration()
    {
        using var f = Prepare();
        var (store, _, module, container) = Start(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        store.Begin(f.PilotId, module, container.Id, new HashSet<long> { container.Id }, f.Clock.Now);
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        store.Cancel(f.PilotId, f.Clock.Now);
        snapshot.Project();
        using WorldDbContext captured = new(snapshot.Database);
        Assert.Equal(container.Id, captured.Set<SavedHackingAttempt>().Single().ContainerId);
        Assert.Equal(0, captured.Set<SavedHackingContainer>().Find(container.Id)!.Failures);
        using WorldDbContext current = new(f.Db.Path);
        Assert.Empty(current.Set<SavedHackingAttempt>());
        Assert.Equal(1, current.Set<SavedHackingContainer>().Find(container.Id)!.Failures);
    }

    [Fact]
    public void AdmittedContentUsesPinnedHackableTypesAndSharedWorldOwner()
    {
        using var f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.Equal(209, HackingCatalog.Sites.Count);
        Assert.Equal(HackingCatalog.Sites.Count, HackingCatalog.Sites.Select(s => s.DungeonId).Distinct().Count());
        foreach (var site in HackingCatalog.Sites)
        {
            Assert.InRange(site.Band, 0, 2);
            Assert.True(site.NameId > 0);
            foreach (var container in site.Containers)
            {
                Assert.Equal(HackingRules.ContainerGroup, catalog.FittingType(container.TypeId)!.GroupId);
                Assert.InRange(container.Difficulty, 0, 5);
                Assert.True(ProbeScanRules.Finite(container.Position));
            }
        }
        using var scope = f.Db.Services.CreateScope();
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<IPilotHacking>(),
            scope.ServiceProvider.GetRequiredService<IPirateExploration>()
        );
    }

    [Theory]
    [InlineData(22175, ExplorationKind.Data)]
    [InlineData(22177, ExplorationKind.Relic)]
    [InlineData(30832, ExplorationKind.Relic)]
    [InlineData(30834, ExplorationKind.Data)]
    [InlineData(41533, ExplorationKind.Data)]
    [InlineData(41534, ExplorationKind.Relic)]
    [InlineData(49791, ExplorationKind.Data)]
    [InlineData(85750, ExplorationKind.Relic)]
    [InlineData(93912, ExplorationKind.Relic)]
    public void StandardAndIntegratedAnalyzerVariantsKeepTheirOwnCatalogProfiles(int typeId, ExplorationKind kind)
    {
        using var f = Prepare();
        long module = f.Fit(typeId);
        PirateExplorationStore store = new(new(f.Db.Path));
        var site = store.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now).Single(s => s.Exploration == kind);
        var container = site.Objects[0];
        f.Db.Store.SetPosition(f.ShipId, container.Position, site.SystemId);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var attributes = catalog.FittingAttributes(typeId);
        int coherence = (int)attributes[Eden.Gameplay.Dogma.DogmaAttributeId.VirusCoherence];
        foreach (var requirement in InventoryDefinitions.RequiredSkills)
        {
            if (attributes.GetValueOrDefault(requirement.Skill) is HackingRules.HackingSkill or HackingRules.ArchaeologySkill)
            {
                coherence += 50;
            }
        }
        var profile = store.Begin(f.PilotId, module, container.Id, new HashSet<long> { container.Id }, f.Clock.Now).Module;
        Assert.Equal(coherence, profile.Coherence);
        Assert.Equal((int)attributes[Eden.Gameplay.Dogma.DogmaAttributeId.VirusStrength], profile.Strength);
        Assert.Equal((int)attributes[Eden.Gameplay.Dogma.DogmaAttributeId.VirusSlots], profile.Slots);
    }

    [Fact]
    public void ExpiredSitesCatchUpFromTheirSavedReplacementDeadlineWithoutAnExtraWait()
    {
        using var f = Prepare();
        PirateExplorationStore store = new(new(f.Db.Path));
        var original = store.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now).ToArray();
        var replacement = store.Signatures(
            CombatAnomalyRules.SystemId,
            f.Clock.Now + HackingRules.SiteLifetime + HackingRules.ReplacementDelay
        );
        Assert.Equal(2, replacement.Count);
        Assert.DoesNotContain(replacement, s => original.Any(old => old.Id == s.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LosingTheLockOrStartingWarpAbandonsTheBoardExactlyOnce(bool warp)
    {
        using var f = Prepare();
        var (_, site, module, container) = Start(f);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new SharedSpace(), site.Objects),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var targets = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        targets.Call(context, "dogmaIM", "AddTarget", [container.Id]);
        f.Clock.Milliseconds += 60000;
        targets.Advance(context);
        var handler = scope.ServiceProvider.GetRequiredService<HackingHandler>();
        Assert.IsType<PilotCallResult.Success>(handler.CallModule(context, "Activate", [module, "doHacking", container.Id, 0L]));
        if (warp)
        {
            Assert.IsType<PilotCallResult.Success>(
                context.Flight.Execute(new FlightCommand.WarpToPoint(-1, container.Position + new SpacePosition(1_000_000_000, 0, 0), 0))
            );
        }
        else
        {
            targets.Call(context, "dogmaIM", "RemoveTarget", [container.Id]);
        }
        handler.Advance(context);
        handler.Advance(context);
        Assert.False(handler.IsMonitoring);
        Assert.Equal(HackingOutcome.Lost, context.Notifications.OfType<HackingTurnCompleted>().Last().Board.Outcome);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedHackingAttempt>());
        Assert.Equal(1, world.Set<SavedHackingContainer>().Find(container.Id)!.Failures);
        Assert.NotNull(world.Set<ModuleCooldown>().Find(module));
    }
}
