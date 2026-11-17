using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class RegionalConflictWorldTests
{
    private const int Home = 30000141;
    private const int Away = 30000142;
    private const int HomeStation = 60015157;
    private const long Human = 90000001;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstLossUsesTheCommunityRegionWhenItsHomeStationIsInAnotherRegion(bool botVictim)
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true, splitRegions: true);
        int regionId = fixture.Database.Catalog.Systems[Home].RegionId;
        Assert.NotEqual(regionId, fixture.Database.Catalog.Systems[Away].RegionId);
        long bot = fixture.Live.Economy.Read(world =>
            world.Set<SavedCapsuleerAgent>().First(agent => agent.CommunitySystemId == Home).PilotId
        );
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            action.Set<SavedCapsuleerPopulation>().Find(Home)!.HomeStationId = 60003760;
            var position = SystemObjects.Geometry(catalog, Home).First(body => body.DestinationGateId is not null).Position;
            foreach (long pilotId in new[] { bot, Human })
            {
                var pilot = action.Pilots.Find(pilotId)!;
                if (pilotId == Human)
                {
                    pilot.ActiveShip.TypeId = 603;
                }
                SqliteWorld.SetPosition(action, catalog, pilot.ActiveShipId, position, Home, fixture.Clock.Now);
            }
            action.Commit();
        }
        Assert.Empty(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts());
        long victimId = botVictim ? bot : Human;
        long attackerId = botVictim ? Human : bot;
        long ship = fixture.Live.Economy.Read(world => world.Pilots.Find(victimId)!.ActiveShipId);
        void Lose(bool commit)
        {
            using var catalog = new CatalogDbContext(fixture.Database.CatalogPath);
            using var action = fixture.Live.Economy.Begin();
            var victim = action.Pilots.Find(victimId)!;
            var attacker = action.Pilots.Find(attackerId)!;
            ShipLossPersistence.Apply(
                action,
                catalog,
                victim,
                null,
                ship,
                Home,
                victim.ActiveShip.Position!,
                fixture.Clock.Now,
                new DropNothing(),
                PilotInteractionHistoryStore.Player(action, attacker, attacker.ActiveShipId)
            );
            if (commit)
            {
                action.Commit();
            }
        }
        Lose(commit: false);
        Assert.True(fixture.Live.Economy.ActiveShip(victimId, ship));
        Assert.Empty(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts());
        Lose(commit: true);
        Assert.False(fixture.Live.Economy.ActiveShip(victimId, ship));
        Assert.Single(fixture.Live.Economy.Read(world => world.Set<ShipLoss>().Where(loss => loss.ShipId == ship).ToArray()));
        var region = Assert.Single(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts());
        Assert.Equal(regionId, region.RegionId);
        var group = Assert.Single(region.Groups);
        Assert.Equal(botVictim ? 1 : 0, group.Losses);
        Assert.Equal(botVictim ? 0 : 1, group.Kills);
        Assert.Contains(ship, region.RecentLosses);
        await fixture.Live.FlushAsync();
        fixture.Restart();
        var restored = Assert.Single(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts());
        Assert.Equal(region.RegionId, restored.RegionId);
        Assert.Equal(region.Groups, restored.Groups);
        Assert.Equal(region.RecentLosses, restored.RecentLosses);
    }

    [Fact]
    public async Task RivalCommunitiesLaunchOnePaidRaidAndRetainItsObjectiveAcrossRestart()
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true);
        Assert.NotEqual(fixture.Database.Catalog.Systems[Home].ConstellationId, fixture.Database.Catalog.Systems[Away].ConstellationId);
        Assert.Equal(fixture.Database.Catalog.Systems[Home].RegionId, fixture.Database.Catalog.Systems[Away].RegionId);
        Assert.NotNull(fixture.Live.Economy.Peek<SavedCapsuleerPopulation>(Away));
        long homePilot = fixture.Live.Economy.Read(world =>
            world.Set<SavedCapsuleerAgent>().First(agent => agent.CommunitySystemId == Home).PilotId
        );
        Assert.Empty(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts());
        Assert.Equal(
            fixture.Database.Catalog.Systems[Home].ConstellationId,
            RegionalConflictStore.GroupForPilot(fixture.Live, fixture.Database.Catalog, homePilot)
        );
        var gang = fixture.Form();
        var order = Assert.IsType<RegionalOperation>(gang.Regional);
        Assert.Equal(RegionalObjective.Raid, order.Objective);
        Assert.Equal(Away, order.TargetSystemId);
        Assert.NotEqual(0, order.TargetGridId);
        var diagnostics = fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>();
        var region = Assert.Single(diagnostics.ReadConflicts());
        Assert.Equal(2, region.Groups.Length);
        Assert.Single(region.Relationships);
        Assert.Single(region.Operations);
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            Assert.Null(RegionalConflictStore.Plan(action, catalog, fixture.Live, HomeStation, RegionalConflictRules.LaunchInterval));
        }
        PilotSighting Contact(int? group) =>
            new(123, 456, 1, HullClass.Frigate, 0, null, new(0, 0, 0), new(0, 0, 0), 1, RegionalGroupId: group);
        using (var metrics = SessionPerformance.Begin("warm regional diagnostics and targeting", _ => { }, force: true)!)
        {
            for (int read = 0; read < 20; read++)
            {
                Assert.Same(region, Assert.Single(diagnostics.ReadConflicts()));
                Assert.False(RegionalConflictStore.CanTarget(fixture.Database, order, Contact(order.Key.GroupId), 0));
                Assert.False(RegionalConflictStore.CanTarget(fixture.Database, order, Contact(null), 0));
                Assert.True(RegionalConflictStore.CanTarget(fixture.Database, order, Contact(order.OpponentGroupId), 0));
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        for (int turn = 0; turn < 500 && RegionalConflictStore.Operation(fixture.Database, order.Key)?.ReachedObjective != true; turn++)
        {
            fixture.Tick();
        }
        var arrived = RegionalConflictStore.Operation(fixture.Database, order.Key)!;
        Assert.True(
            arrived.ReachedObjective,
            string.Join(
                Environment.NewLine,
                fixture
                    .Live.HaulerCheckpoints.Values.Where(value => value.Work.Gang?.Id == gang.Id)
                    .Select(value => $"{value.PilotId}: {value.Work.Activity} {value.Work.WaitingReason}, {value.Work.Ratting}")
            )
        );
        Assert.DoesNotContain(Away, fixture.Host.GetRequiredService<IHumanPresence>().Read().ActiveSystems);
        await fixture.Live.FlushAsync();
        fixture.Restart();
        _ = fixture.Scheduler.Jobs();
        var restored = Assert.Single(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts());
        Assert.Equal(arrived.Key, Assert.Single(restored.Operations).Key);
        Assert.Equal(arrived.ExpiresAt, restored.Operations[0].ExpiresAt);
        Assert.True(restored.Operations[0].ReachedObjective);
        fixture.PauseHuman();
        fixture.Tick();
        var before = fixture.Live.CapsuleerJobs.ToDictionary(pair => pair.Key, pair => pair.Value);
        for (int turn = 0; turn < 20; turn++)
        {
            fixture.Tick();
        }
        Assert.All(before, pair => Assert.Equal(pair.Value, fixture.Live.CapsuleerJobs[pair.Key]));
        Assert.Equal(0, fixture.Scheduler.Read().Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealLossAndRegionalPressureCommitTogetherAndRollbackLeavesBothUnchanged(bool botAttacker)
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true);
        var gang = fixture.Form();
        var order = gang.Regional!;
        for (int turn = 0; turn < 100 && fixture.Scheduler.Hauler(gang.LeaderId)!.Pilot.Position is null; turn++)
        {
            fixture.Tick();
        }
        var victim = fixture.Scheduler.Hauler(gang.LeaderId)!.Pilot;
        Assert.NotNull(victim.Position);
        var prior = RegionalConflictStore.Operation(fixture.Database, order.Key)!;
        long attackerId = botAttacker
            ? fixture.Live.Economy.Read(world => world.Set<SavedCapsuleerAgent>().First(agent => agent.CommunitySystemId == Away).PilotId)
            : Human;
        void Lose(bool commit)
        {
            using var catalog = new CatalogDbContext(fixture.Database.CatalogPath);
            using var action = fixture.Live.Economy.Begin();
            var pilot = action.Pilots.Find(victim.PilotId)!;
            var attacker = action.Pilots.Find(attackerId)!;
            ShipLossPersistence.Apply(
                action,
                catalog,
                pilot,
                null,
                victim.ShipId,
                victim.SystemId,
                pilot.ActiveShip.Position!,
                fixture.Clock.Now,
                new DropNothing(),
                PilotInteractionHistoryStore.Player(action, attacker, attacker.ActiveShipId)
            );
            if (commit)
            {
                action.Commit();
            }
        }
        Lose(commit: false);
        Assert.True(fixture.Live.Economy.ActiveShip(victim.PilotId, victim.ShipId));
        Assert.Empty(fixture.Live.Economy.Read(world => world.Set<ShipLoss>().ToArray()));
        Assert.Same(prior, RegionalConflictStore.Operation(fixture.Database, order.Key));
        Lose(commit: true);
        Assert.False(fixture.Live.Economy.ActiveShip(victim.PilotId, victim.ShipId));
        Assert.Single(fixture.Live.Economy.Read(world => world.Set<ShipLoss>().ToArray()));
        var region = Assert.Single(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts());
        var home = region.Groups.Single(group => group.Id == order.Key.GroupId);
        Assert.Equal(1, home.Losses);
        Assert.True(home.NextOffensiveAt >= RegionalConflictRules.LossRecovery);
        var report = Assert.Single(region.Reports, report => report.Kind == RegionalReportKind.Loss);
        Assert.Equal(attackerId, report.PilotId);
        Assert.Equal(botAttacker ? 1 : 0, region.Groups.Sum(group => group.Kills));
        Assert.Equal(RegionalReportKind.Loss, report.Kind);
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            RegionalConflictStore.Loss(action, catalog, victim.PilotId, victim.ShipId, victim.SystemId, victim.Position!, attackerId);
            action.Commit();
        }
        Assert.Equal(
            1,
            Assert
                .Single(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts())
                .Groups.Single(group => group.Id == order.Key.GroupId)
                .Losses
        );
        await fixture.Live.FlushAsync();
        fixture.Restart();
        var restored = Assert.Single(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts());
        Assert.Equal(home.NextOffensiveAt, restored.Groups.Single(group => group.Id == home.Id).NextOffensiveAt);
        Assert.Equal(region.RecentLosses, restored.RecentLosses);
    }

    [Fact]
    public void WitnessedRivalIntrusionLaunchesALocalDefenseInsteadOfARaid()
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true);
        int group = fixture.Database.Catalog.Systems[Home].ConstellationId;
        int rival = fixture.Database.Catalog.Systems[Away].ConstellationId;
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            _ = RegionalConflictStore.Plan(action, catalog, fixture.Live, HomeStation, 0);
            RegionalConflictStore.Report(
                action,
                catalog,
                HomeStation,
                new(group, rival, 123, 456, Home, 50009991, RegionalReportKind.CombatShip, 0, RegionalConflictRules.ReportTicks)
            );
            action.Commit();
        }
        var gang = fixture.Form();
        Assert.Equal(RegionalObjective.Defend, gang.Regional!.Objective);
        Assert.Equal(Home, gang.Regional.TargetSystemId);
        Assert.Equal(123, gang.Regional.TargetPilotId);
        Assert.Equal(gang.HomeStationId, gang.DestinationStationId);
        for (int turn = 0; turn < 200 && !RegionalConflictStore.Operation(fixture.Database, gang.Regional.Key)!.ReachedObjective; turn++)
        {
            fixture.Tick();
        }
        Assert.True(RegionalConflictStore.Operation(fixture.Database, gang.Regional.Key)!.ReachedObjective);
        Assert.Equal(0, fixture.Scheduler.Read().Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EscortFollowsOwnedFreightAndCompletesOnlyAfterItsActualDelivery(bool missingCargo)
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true);
        long pilotId;
        long cargoId;
        long shipId;
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            var agent = action
                .Set<SavedCapsuleerAgent>()
                .Where(agent => agent.CommunitySystemId == Home)
                .OrderBy(agent => agent.PilotId)
                .AsEnumerable()
                .First(agent => action.Set<SavedCapsuleerSuspension>().Find(agent.PilotId) is not null);
            pilotId = agent.PilotId;
            var pilot = action.Pilots.Find(pilotId)!;
            shipId = pilot.ActiveShipId;
            pilot.ActiveShip.TypeId = HaulingRules.BadgerType;
            pilot.ActiveShip.StationId = HomeStation;
            action.Remove(action.Set<SavedCapsuleerSuspension>().Find(pilotId)!);
            var gate = SystemObjects.Geometry(catalog, Home).Single(body => body.Id == 50009991);
            SqliteWorld.SetPosition(action, catalog, shipId, gate.Position + new SpacePosition(200_000, 0, 0), Home, fixture.Clock.Now);
            cargoId = InventoryPersistence.NextId(action);
            action.Add(
                new OwnedItem
                {
                    Id = cargoId,
                    OwnerId = pilotId,
                    LocationId = shipId,
                    TypeId = 215,
                    Quantity = 10,
                    FlagId = (int)InventoryFlag.Cargo,
                }
            );
            var work = new HaulerProgress(HaulerActivity.Waiting, 0, 0, shipId, 60003760, 50009991, new NavigationState().Capture())
            {
                Journey = new(TrafficJourneyKind.Regional, HomeStation, HomeStation, 60003760, [Home, Away], 1),
                Freight = new(1, 60003760, [new(cargoId, 215, 10)]),
            };
            var row = action.Set<SavedCapsuleerHauler>().Single(row => row.PilotId == pilotId);
            row.SourceStationId = HomeStation;
            row.DestinationStationId = 60003760;
            row.RouteSystems = [Home, Away];
            new HaulerCheckpoint(pilotId, work).Apply(action);
            action.Commit();
            fixture.Live.HaulerCheckpoints[pilotId] = new(pilotId, work);
        }
        var gang = fixture.Form();
        var order = Assert.IsType<RegionalOperation>(gang.Regional);
        Assert.Equal(RegionalObjective.Escort, order.Objective);
        Assert.Equal(pilotId, order.TargetPilotId);
        var assignment = Assert.IsType<RegionalEscort>(fixture.Live.HaulerCheckpoints[pilotId].Work.RegionalEscort);
        Assert.Equal(shipId, assignment.ShipId);
        Assert.Equal(4, assignment.Guards.Length);
        await fixture.Live.FlushAsync();
        fixture.Restart();
        _ = fixture.Scheduler.Jobs();
        Assert.Equal(order.Key, fixture.Live.HaulerCheckpoints[pilotId].Work.RegionalEscort!.Operation.Key);
        if (missingCargo)
        {
            using var action = fixture.Live.Economy.Begin();
            action.Remove(action.Set<OwnedItem>().Find(cargoId)!);
            action.Commit();
        }
        bool reachedUnobserved = false;
        for (
            int turn = 0;
            turn < 1100 && RegionalConflictStore.Operation(fixture.Database, order.Key)!.Status == RegionalOperationStatus.Active;
            turn++
        )
        {
            fixture.Tick();
            var observation = fixture.Scheduler.Hauler(pilotId)!;
            reachedUnobserved |= observation.Pilot.SystemId == Away;
        }
        var result = RegionalConflictStore.Operation(fixture.Database, order.Key)!;
        Assert.True(
            result.Status == (missingCargo ? RegionalOperationStatus.Aborted : RegionalOperationStatus.Completed),
            $"{result.Status}: {result.Reason}, freight {fixture.Live.HaulerCheckpoints[pilotId].Work.Activity}, "
                + $"target {fixture.Live.HaulerCheckpoints[pilotId].Work.TargetId}, "
                + string.Join(
                    Environment.NewLine,
                    gang.Members.Select(member =>
                        $"{member.Role}: {fixture.Live.HaulerCheckpoints[member.PilotId].Work.WaitingReason}, "
                        + $"{fixture.Live.HaulerCheckpoints[member.PilotId].Work.Gang?.Reason}, "
                        + $"{fixture.Live.HaulerCheckpoints[member.PilotId].Work.Activity}, "
                        + $"{fixture.Live.HaulerCheckpoints[member.PilotId].Work.TargetId}"
                    )
                )
        );
        Assert.True(reachedUnobserved);
        Assert.Null(fixture.Live.Economy.Read(world => world.Set<OwnedItem>().Find(cargoId)));
        Assert.Null(fixture.Live.HaulerCheckpoints[pilotId].Work.RegionalEscort);
        Assert.Equal(0, fixture.Scheduler.Read().Failures);
    }

    [Fact]
    public void RivalDefenseEngagesAVisibleRaidWithOwnedWeaponsAndRealDamage()
    {
        using var fixture = new RoamingGangTests.Fixture(regional: true);
        var raid = fixture.Form();
        int defenderGroup = fixture.Database.Catalog.Systems[Away].ConstellationId;
        long[] defenders;
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            defenders = action
                .Set<SavedCapsuleerAgent>()
                .Where(agent => agent.CommunitySystemId == Away)
                .OrderBy(agent => agent.PilotId)
                .Take(4)
                .Select(agent => agent.PilotId)
                .ToArray();
            Assert.Equal(4, defenders.Length);
            foreach (long pilotId in defenders)
            {
                var pilot = action.Pilots.Find(pilotId)!;
                if (action.Positions.Find(pilot.ActiveShipId) is { } position)
                {
                    action.Remove(position);
                }
                pilot.ActiveShip.StationId = 60003760;
                if (action.Set<SavedCapsuleerSuspension>().Find(pilotId) is { } suspended)
                {
                    action.Remove(suspended);
                }
                var row = action.Set<SavedCapsuleerHauler>().Single(row => row.PilotId == pilotId);
                row.SourceStationId = 60003760;
                row.DestinationStationId = HomeStation;
                row.RouteSystems = [Away, Home];
                row.Progress = null;
            }
            // Seed the defender's witnessed-incursion input; subsequent targets require real visible ships.
            RegionalConflictStore.Report(
                action,
                catalog,
                60003760,
                new(
                    defenderGroup,
                    raid.Regional!.Key.GroupId,
                    raid.LeaderId,
                    raid.Members.Single(member => member.PilotId == raid.LeaderId).ShipId,
                    Away,
                    raid.Regional.TargetGridId,
                    RegionalReportKind.CombatShip,
                    0,
                    RegionalConflictRules.ReportTicks
                )
            );
            action.Commit();
        }
        using var defenderObserver = fixture.Host.GetRequiredService<IHumanPresence>().Select(Human + 1, Away);
        fixture.Scheduler.PopulationPrepared(Away);
        RoamingGang? response = null;
        for (int turn = 0; turn < 120 && response is null; turn++)
        {
            fixture.Tick();
            response = defenders
                .Select(pilot => fixture.Live.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work.Gang)
                .FirstOrDefault(gang => gang is not null);
        }
        Assert.NotNull(response);
        Assert.Equal(RegionalObjective.Defend, response.Regional!.Objective);
        defenderObserver.Dispose();
        var health = fixture.Host.GetRequiredService<Eden.Gameplay.Combat.IPilotHealth>();
        double Remaining() =>
            defenders.Sum(pilot =>
            {
                var current = health.Read(pilot, fixture.Clock.Now).Remaining;
                return current.Shield + current.Armor + current.Structure;
            });
        double before = Remaining();
        var modules = raid
            .Members.SelectMany(member => fixture.Host.GetRequiredService<CapsuleerDefense>().Profile(member.PilotId).Modules)
            .Where(module => module.Effect is DogmaEffect.ProjectileFired or DogmaEffect.LaserFired)
            .Select(module => module.Item.Id)
            .ToArray();
        long Ammo() =>
            fixture.Live.Economy.Read(world =>
                world.Set<OwnedItem>().Where(item => Enumerable.Contains(modules, item.LocationId)).Sum(item => (long)item.Quantity)
            );
        long ammo = Ammo();
        bool targeted = false;
        for (int turn = 0; turn < 350 && (Remaining() >= before || Ammo() >= ammo); turn++)
        {
            fixture.Tick();
            targeted |= raid.Members.Any(member =>
                fixture.Live.HaulerCheckpoints[member.PilotId].Work.Defense is { } state
                && response.Members.Any(enemy => enemy.ShipId == state.TargetId)
            );
        }
        Assert.True(targeted);
        Assert.True(Remaining() < before);
        Assert.True(Ammo() < ammo, $"Loaded charges {ammo} -> {Ammo()}, weapon modules {modules.Length}.");
        Assert.Contains(
            Assert.Single(fixture.Host.GetRequiredService<IRegionalConflictDiagnostics>().ReadConflicts()).Reports,
            report =>
                report.ObserverGroupId == raid.Regional!.Key.GroupId
                && report.OtherGroupId == defenderGroup
                && response.Members.Any(member => member.ShipId == report.ShipId)
        );
        Assert.DoesNotContain(Away, fixture.Host.GetRequiredService<IHumanPresence>().Read().ActiveSystems);
        Assert.Equal(0, fixture.Scheduler.Read().Failures);
    }

    [Fact]
    public void ExistingWorldUpgradeAddsEmptyConflictStateAndPreservesPilots()
    {
        using var fixture = new CapsuleerHaulerTests.Fixture(legacyFounders: false, isolateRoute: true);
        int pilots;
        using (var world = new WorldDbContext(fixture.Database.Path))
        {
            pilots = world.Pilots.Count();
            world.Database.ExecuteSqlRaw("DROP TABLE WorldRegionalConflict; PRAGMA user_version=184;");
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var upgraded = new WorldDbContext(fixture.Database.Path);
        Assert.Empty(upgraded.Set<SavedRegionalConflict>());
        Assert.Equal(pilots, upgraded.Pilots.Count());
        Assert.Equal(WorldMigration.CurrentVersion, upgraded.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    private sealed class DropNothing : IShipLootRoll
    {
        public bool Survives() => false;
    }
}
