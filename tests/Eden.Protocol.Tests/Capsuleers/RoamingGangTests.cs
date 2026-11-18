using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class RoamingGangTests
{
    private const int Home = 30000141;
    private const int Away = 30000142;
    private const int HomeStation = 60015157;
    private const int AwayStation = 60003760;
    private const long Human = 90000001;

    [Fact]
    public void FormationReservesTheOnlyTacklerAndDamageDealerWithinTheConfiguredSize()
    {
        static RoamingGangRecruit Recruit(long id, bool tackle = false, bool damage = true) =>
            new(new(id, id, PvpRole.Brawler) { DamageCapable = damage }, id, tackle);

        // The leader and first two recruits have no point. The fourth is the fastest and the only tackler.
        RoamingGangRecruit[] available = [Recruit(1), Recruit(2), Recruit(3), Recruit(4, tackle: true)];
        Assert.Empty(RoamingGangRules.SelectRoster(available[..3], 3));
        var roster = RoamingGangRules.SelectRoster(available, 3);
        Assert.Equal([1L, 3L, 4L], roster.Select(member => member.PilotId));
        Assert.Equal(4, Assert.Single(roster, member => member.Role == PvpRole.Tackle).PilotId);
        Assert.Equal(3, Assert.Single(roster, member => member.Role == PvpRole.Scout).PilotId);

        available = [Recruit(1, damage: false), Recruit(2, tackle: true, damage: false), Recruit(3)];
        roster = RoamingGangRules.SelectRoster(available, 3);
        Assert.Equal(1, Assert.Single(roster, member => member.Role == PvpRole.Scout).PilotId);
        Assert.Equal(3, Assert.Single(roster, member => member.DamageCapable).PilotId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void StationFormsAndLaunchesThreeShipsWithOneFittedTackler(int tackleSlot)
    {
        using var fixture = new Fixture(gangSize: 3);
        long tackler;
        using (var action = fixture.Live.Economy.Begin())
        {
            var crew = action
                .Set<SavedCapsuleerHauler>()
                .Where(row => row.SourceStationId == HomeStation)
                .AsEnumerable()
                .Where(row => action.Set<SavedCapsuleerSuspension>().Find(row.PilotId) is null)
                .OrderBy(row => row.PilotId)
                .ToArray();
            Assert.Equal(4, crew.Length);
            tackler = crew[tackleSlot].PilotId;
            foreach (var row in crew.Where(row => row.PilotId != tackler))
            {
                var point = action.Set<OwnedItem>().Single(item => item.OwnerId == row.PilotId && item.Online && item.TypeId == 447);
                point.Online = false;
            }
            action.Commit();
        }
        var gang = fixture.Form();
        Assert.Equal(3, gang.Members.Length);
        Assert.Equal(tackler, Assert.Single(gang.Members, member => member.Role == PvpRole.Tackle).PilotId);
        Assert.NotEqual(tackler, Assert.Single(gang.Members, member => member.Role == PvpRole.Scout).PilotId);
        var launched = new HashSet<long>();
        for (int turn = 0; turn < 120 && launched.Count < gang.Members.Length; turn++)
        {
            fixture.Tick();
            foreach (var member in gang.Members.Where(member => fixture.Scheduler.Hauler(member.PilotId)!.Pilot.Position is not null))
            {
                launched.Add(member.PilotId);
            }
        }
        Assert.Equal(gang.Members.Length, launched.Count);
    }

    [Fact]
    public void ScoutCommitsOnlyWithVisibleActiveDamageSupportWithinReachOfTheOpponent()
    {
        var scout = new RoamingGangMember(1, 11, PvpRole.Scout) { Hull = HullClass.Frigate };
        var wing = new RoamingGangMember(2, 22, PvpRole.Tackle) { Hull = HullClass.Frigate };
        PilotSighting At(long pilot, long ship, double x) =>
            new(pilot, ship, 603, HullClass.Frigate, 0, null, new(x, 0, 0), SpacePosition.Zero, 0);
        var target = At(3, 33, 1_000);
        var nearby = At(2, 22, target.Position.X + RoamingGangRules.ScoutBackupMeters);
        var gang = new RoamingGang(1, 1, 1, 1, 2, [Home, Away], 1, [scout, wing], 0, 10) { Phase = RoamingGangPhase.Hunting };
        bool Commit(RoamingGang command, PilotSighting[] seen, double range = 2_000, double threat = 1) =>
            RoamingGangRules.CanScoutTackle(command, scout, SpacePosition.Zero, 0, 0, range, target, seen, threat);

        Assert.True(Commit(gang, [nearby]));
        Assert.False(Commit(gang, []));
        Assert.False(Commit(gang, [nearby with { Position = nearby.Position + new SpacePosition(1, 0, 0) }]));
        Assert.False(Commit(gang, [nearby with { ShipId = 99 }]));
        Assert.False(Commit(gang, [nearby], range: 0));
        Assert.False(Commit(gang, [nearby], range: 999));
        Assert.False(Commit(gang, [nearby], threat: PvpTactics.Threat(HullClass.Battleship)));
        Assert.False(Commit(gang with { Members = [scout, wing with { DamageCapable = false }] }, [nearby]));
        Assert.False(Commit(gang with { Members = [scout, wing with { Status = RoamingGangMemberStatus.Lost }] }, [nearby]));
        Assert.False(Commit(gang with { Phase = RoamingGangPhase.Returning }, [nearby]));
    }

    [Fact]
    public async Task GangFormsTravelsWithoutAnObserverAndReturnsAfterRestart()
    {
        using var fixture = new Fixture();
        RoamingGang? original = null;
        for (int turn = 0; turn < 120; turn++)
        {
            fixture.Tick();
            original = fixture
                .Live.HaulerCheckpoints.Values.Select(checkpoint => checkpoint.Work.Gang)
                .FirstOrDefault(gang => gang is not null);
            if (original is not null)
            {
                break;
            }
        }
        Assert.True(
            original is not null,
            string.Join(
                Environment.NewLine,
                fixture.Live.HaulerCheckpoints.Values.Select(value =>
                    $"{value.PilotId}: {value.Work.Activity}, {value.Work.WaitingReason}, gang {value.Work.Gang}"
                )
            )
        );
        Assert.Equal(4, original.Members.Length);
        Assert.Contains(original.Members, member => member.Role == PvpRole.Scout);
        Assert.Contains(original.Members, member => member.Role == PvpRole.Tackle);
        Assert.All(original.Members, member => Assert.Equal(original.Id, fixture.Live.HaulerCheckpoints[member.PilotId].Work.Gang!.Id));
        var ids = original.Members.Select(member => member.PilotId).ToArray();
        var visited = new HashSet<long>();
        for (int turn = 0; turn < 240; turn++)
        {
            fixture.Tick();
            foreach (long pilot in ids)
            {
                if (fixture.Scheduler.Hauler(pilot)!.Pilot.SystemId == Away)
                {
                    visited.Add(pilot);
                }
            }
            if (visited.Count > 0)
            {
                break;
            }
        }
        Assert.NotEmpty(visited);
        Assert.DoesNotContain(Away, fixture.Host.GetRequiredService<IHumanPresence>().Read().ActiveSystems);
        var before = fixture.Live.HaulerCheckpoints[original.Id].Work.Gang!;
        await fixture.Live.FlushAsync();
        fixture.Restart();
        _ = fixture.Scheduler.Jobs();
        var restored = fixture.Live.HaulerCheckpoints[original.Id].Work.Gang!;
        Assert.Equal(before.Id, restored.Id);
        Assert.Equal(before.Sequence, restored.Sequence);
        Assert.Equal(before.LeaderId, restored.LeaderId);
        Assert.Equal(before.Members, restored.Members);
        Assert.Equal(before.Systems, restored.Systems);
        for (int turn = 0; turn < 1200; turn++)
        {
            fixture.Tick();
            foreach (long pilot in ids)
            {
                if (fixture.Scheduler.Hauler(pilot)!.Pilot.SystemId == Away)
                {
                    visited.Add(pilot);
                }
            }
            if (ids.All(pilot => fixture.Live.HaulerCheckpoints[pilot].Work.Gang is { Phase: RoamingGangPhase.Complete }))
            {
                break;
            }
        }
        Assert.Equal(ids.Length, visited.Count);
        Assert.All(
            ids,
            pilot =>
            {
                var observation = fixture.Scheduler.Hauler(pilot)!;
                Assert.True(
                    observation.Work.Gang?.Phase == RoamingGangPhase.Complete,
                    $"Pilot {pilot}: {observation.Work.Gang}, station {observation.Pilot.StationId}, work {observation.Work.Activity}, "
                        + $"due {observation.Work.DueTicks}, tick {observation.Work.ActiveTicks}, {observation.Work.WaitingReason}, "
                        + string.Join(',', observation.Work.Gang!.Members.Select(member => $"{member.PilotId}:{member.Status}"))
                );
                Assert.Null(observation.Pilot.Position);
                Assert.Equal(HomeStation, observation.Pilot.StationId);
                Assert.Equal(
                    RoamingGangMemberStatus.Returned,
                    observation.Work.Gang!.Members.Single(member => member.PilotId == pilot).Status
                );
            }
        );
        Assert.Equal(0, fixture.Scheduler.Read().Failures);
    }

    [Fact]
    public void RejectedGangPublicationPreservesMembersAndWarmPublicationUsesNoSql()
    {
        using var fixture = new Fixture();
        for (int turn = 0; turn < 120 && !fixture.Live.HaulerCheckpoints.Values.Any(checkpoint => checkpoint.Work.Gang is not null); turn++)
        {
            fixture.Tick();
        }
        var checkpoint = fixture.Live.HaulerCheckpoints.Values.First(value => value.Work.Gang is not null);
        var gang = checkpoint.Work.Gang!;
        var nextGang = RoamingGangRules.Report(gang, 99, Home, 50009991, 0);
        var job = fixture.Live.CapsuleerJobs[checkpoint.PilotId];
        using (var rejected = new CapsuleerOperation(fixture.Database, job, fixture.Host.GetRequiredService<MarketChangeSignal>()))
        {
            rejected.GangEnrollments.Add(checkpoint with { Work = checkpoint.Work with { Gang = nextGang } });
        }
        Assert.Same(gang, fixture.Live.HaulerCheckpoints[checkpoint.PilotId].Work.Gang);
        using (
            var stale = new CapsuleerOperation(
                fixture.Database,
                job with
                {
                    Revision = job.Revision - 1,
                },
                fixture.Host.GetRequiredService<MarketChangeSignal>()
            )
        )
        {
            Assert.Throws<InvalidOperationException>(() =>
                stale.Commit(job.Next(job.DueTicks, job.Action), null, checkpoint with { Work = checkpoint.Work with { Gang = nextGang } })
            );
        }
        Assert.All(gang.Members, member => Assert.Same(gang, fixture.Live.HaulerCheckpoints[member.PilotId].Work.Gang));
        using (var metrics = SessionPerformance.Begin("warm gang publication", _ => { }, force: true)!)
        {
            using var operation = new CapsuleerOperation(fixture.Database, job, fixture.Host.GetRequiredService<MarketChangeSignal>());
            operation.Commit(job.Next(job.DueTicks, job.Action), null, checkpoint with { Work = checkpoint.Work with { Gang = nextGang } });
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.All(gang.Members, member => Assert.Same(nextGang, fixture.Live.HaulerCheckpoints[member.PilotId].Work.Gang));
        var current = fixture.Live.CapsuleerJobs[checkpoint.PilotId];
        using (var staleWorker = new CapsuleerOperation(fixture.Database, current, fixture.Host.GetRequiredService<MarketChangeSignal>()))
        {
            staleWorker.Commit(current.Next(current.DueTicks, current.Action), null, checkpoint);
        }
        Assert.All(gang.Members, member => Assert.Same(nextGang, fixture.Live.HaulerCheckpoints[member.PilotId].Work.Gang));
        using var captured = Assert.IsType<WorldSnapshot>(fixture.Live.CaptureSnapshot());
        captured.Project();
        using var reopened = new WorldDbContext(captured.Database);
        Assert.All(
            gang.Members,
            member =>
            {
                var restored = HaulerCheckpoint.Read(
                    reopened.Set<SavedCapsuleerHauler>().Single(row => row.PilotId == member.PilotId).Progress
                )!;
                Assert.Equal(nextGang.Revision, restored.Gang!.Revision);
                Assert.Equal(99, restored.Gang.Sighting!.ShipId);
            }
        );
    }

    [Fact]
    public void UnobservedAutonomousContactCanBeLockedTackledAndDamagedWithoutSql()
    {
        using var fixture = new Fixture();
        var gang = fixture.Form();
        long attackerId = gang.Members.Single(member => member.Role == PvpRole.Tackle).PilotId;
        long victimId;
        long victimShip;
        var ships = fixture.Host.GetRequiredService<SqliteWorld>();
        DockedPilot attacker;
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            var victim = action
                .Set<SavedCapsuleerHauler>()
                .OrderBy(row => row.PilotId)
                .First(row => !gang.Members.Any(member => member.PilotId == row.PilotId));
            victimId = victim.PilotId;
            victimShip = action.Pilots.Find(victimId)!.ActiveShipId;
            action.Remove(action.Set<SavedCapsuleerSuspension>().Find(victimId)!);
            var anchor = SystemObjects.Geometry(catalog, Away).Single(body => body.Id == 50009992).Position;
            var attackerRow = action.Pilots.Find(attackerId)!;
            SqliteWorld.SetPosition(action, catalog, attackerRow.ActiveShipId, anchor, Away, fixture.Clock.Now);
            SqliteWorld.SetPosition(action, catalog, victimShip, anchor + new SpacePosition(1_000, 0, 0), Away, fixture.Clock.Now);
            attacker = ships.FindPilot(action, attackerRow.AccountId, attackerId)!;
            action.Commit();
        }
        fixture.Scheduler.PopulationPrepared(Away);
        fixture.Live.HaulerCheckpoints[victimId] = new(
            victimId,
            new(HaulerActivity.Waiting, 0, 0, victimShip, AwayStation, 0, new NavigationState().Capture())
        );
        var original = fixture.Live.HaulerCheckpoints[attackerId];
        fixture.Live.HaulerCheckpoints[attackerId] = original with
        {
            Work = original.Work with { Navigation = new NavigationState().Capture(), Activity = HaulerActivity.Flying, ArrivalUntil = 0 },
        };
        var presence = fixture.Host.GetRequiredService<IPilotCombatPresence>();
        var native = fixture.Host.GetRequiredService<ICapsuleerScene>();
        native.Apply(new(new(0, 0, null, false), [], []) { Revision = long.MaxValue });
        Assert.Equal(0, native.MaterializedPilots);
        Assert.NotNull(presence.FindOnline(Away, victimShip));
        Assert.Contains(presence.Observe(Away, attacker.ShipId, attacker.Position!, 0), contact => contact.ShipId == victimShip);
        var defense = fixture.Host.GetRequiredService<CapsuleerDefense>();
        var health = fixture.Host.GetRequiredService<IPilotHealth>();
        var before = health.Read(victimId, fixture.Clock.Now).Remaining;
        var modules = defense
            .Profile(attackerId)
            .Modules.Where(module => module.Effect is DogmaEffect.LaserFired or DogmaEffect.ProjectileFired)
            .Select(module => module.Item.Id)
            .ToArray();
        long Charges() =>
            fixture.Live.Economy.Read(action =>
                action.Set<OwnedItem>().Where(item => Enumerable.Contains(modules, item.LocationId)).Sum(item => (long)item.Quantity)
            );
        long charges = Charges();
        NavigationState navigation = new();
        CapsuleerDefenseState? state = null;
        void Fire(int turn)
        {
            fixture.Clock.Seconds++;
            var target = Assert.IsType<CapsuleerAttacker>(
                defense.PatrolTarget(attacker, victimShip, attacker.Position!, fixture.Clock.Now)
            );
            var result = defense.Advance(
                attacker,
                navigation,
                state,
                fixture.Clock.Now,
                turn,
                [],
                proactive: target,
                pvpRole: PvpRole.Tackle
            );
            attacker = result.Pilot;
            state = result.State;
        }
        for (int turn = 0; turn < 8; turn++)
        {
            Fire(turn);
        }
        using (var metrics = SessionPerformance.Begin("unobserved paid gang combat", _ => { }, force: true)!)
        {
            for (int turn = 8; turn < 16; turn++)
            {
                Fire(turn);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        var after = health.Read(victimId, fixture.Clock.Now).Remaining;
        Assert.True(after.Shield + after.Armor + after.Structure < before.Shield + before.Armor + before.Structure);
        Assert.True(Charges() < charges);
        Assert.Contains(
            fixture.Host.GetRequiredService<Eden.Gameplay.Propulsion.IPilotTackle>().Incoming(victimShip),
            effect => effect.SourceShipId == attacker.ShipId
        );
        Assert.Equal(0, native.MaterializedPilots);
        var saved = fixture.Live.HaulerCheckpoints[victimId];
        fixture.Live.HaulerCheckpoints[victimId] = saved with { Work = saved.Work with { Activity = HaulerActivity.Jumping } };
        Assert.Null(presence.FindOnline(Away, victimShip));
        Assert.True(presence.IsAreaProtected(Away, victimShip));
        Assert.DoesNotContain(presence.Observe(Away, attacker.ShipId, attacker.Position!, 0), contact => contact.ShipId == victimShip);
    }

    [Fact]
    public async Task RealLeaderLossElectsASurvivorAndRetainsTheGangOnRestart()
    {
        using var fixture = new Fixture();
        var gang = fixture.Form();
        HaulerObservation leader = fixture.Scheduler.Hauler(gang.LeaderId)!;
        for (int turn = 0; turn < 100 && leader.Pilot.Position is null; turn++)
        {
            fixture.Tick();
            leader = fixture.Scheduler.Hauler(gang.LeaderId)!;
        }
        Assert.NotNull(leader.Pilot.Position);
        using (var catalog = new CatalogDbContext(fixture.Database.CatalogPath))
        using (var action = fixture.Live.Economy.Begin())
        {
            var victim = action.Pilots.Find(gang.LeaderId)!;
            var position = victim.ActiveShip.Position!;
            var attacker = action.Pilots.Find(Human)!;
            ShipLossPersistence.Apply(
                action,
                catalog,
                victim,
                null,
                victim.ActiveShipId,
                leader.Pilot.SystemId,
                position,
                fixture.Clock.Now,
                new DropNothing(),
                PilotInteractionHistoryStore.Player(action, attacker, attacker.ActiveShipId)
            );
            action.Commit();
        }
        for (int turn = 0; turn < 20; turn++)
        {
            fixture.Tick();
            if (
                fixture.Live.HaulerCheckpoints[gang.Id].Work.Gang!.Members.Single(member => member.PilotId == gang.LeaderId).Status
                == RoamingGangMemberStatus.Lost
            )
            {
                break;
            }
        }
        var updated = fixture.Live.HaulerCheckpoints[gang.Id].Work.Gang!;
        Assert.NotEqual(gang.LeaderId, updated.LeaderId);
        Assert.Equal(RoamingGangMemberStatus.Lost, updated.Members.Single(member => member.PilotId == gang.LeaderId).Status);
        Assert.False(fixture.Live.Economy.ActiveShip(gang.LeaderId, leader.Pilot.ShipId));
        Assert.Equal(1, fixture.Live.Economy.Read(action => action.Set<ShipLoss>().Count(loss => loss.ShipId == leader.Pilot.ShipId)));
        await fixture.Live.FlushAsync();
        fixture.Restart();
        _ = fixture.Scheduler.Jobs();
        var restored = fixture.Live.HaulerCheckpoints[gang.Id].Work.Gang!;
        Assert.Equal(updated.LeaderId, restored.LeaderId);
        Assert.Equal(updated.Members, restored.Members);
        Assert.False(fixture.Live.Economy.ActiveShip(gang.LeaderId, leader.Pilot.ShipId));
    }

    private sealed class DropNothing : IShipLootRoll
    {
        public bool Survives() => false;
    }

    [Fact]
    public void GangPausesWhenTheLastHumanLeavesWithoutConsumingItsSortie()
    {
        using var fixture = new Fixture();
        var gang = fixture.Form();
        fixture.PauseHuman();
        fixture.Tick();
        var before = fixture.Live.CapsuleerJobs.ToDictionary(pair => pair.Key, pair => pair.Value);
        long elapsed = fixture.Host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks;
        for (int turn = 0; turn < 30; turn++)
        {
            fixture.Tick();
        }
        Assert.Equal(elapsed, fixture.Host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks);
        Assert.All(before, pair => Assert.Equal(pair.Value, fixture.Live.CapsuleerJobs[pair.Key]));
        Assert.All(gang.Members, member => Assert.Equal(gang.ReturnAt, fixture.Live.HaulerCheckpoints[member.PilotId].Work.Gang!.ReturnAt));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly CapsuleerHaulerTests.Fixture _world = new(legacyFounders: false, isolateRoute: true);
        private IDisposable? _presence;
        internal WorldDatabase Database => _world.Database;
        internal TestClock Clock => _world.Clock;
        internal ServiceProvider Host { get; private set; } = null!;
        internal LiveWorld Live => Host.GetRequiredService<LiveWorld>();
        internal CapsuleerScheduler Scheduler => Host.GetRequiredService<CapsuleerScheduler>();

        internal Fixture(
            bool regional = false,
            bool splitRegions = false,
            bool supportCatalog = false,
            int? targetLimit = null,
            bool thermalCatalog = false,
            int gangSize = 4,
            bool gangFrigates = false
        )
        {
            PassiveCatalogFixture.LoadFrigateHulls(Database.CatalogPath);
            PassiveCatalogFixture.Load(Database.CatalogPath);
            PassiveCatalogFixture.LoadTackleTiers(Database.CatalogPath);
            if (gangFrigates)
            {
                PassiveCatalogFixture.LoadTechTwoHulls(Database.CatalogPath);
                PassiveCatalogFixture.LoadProjectileTiers(Database.CatalogPath, new HashSet<int> { 484, 185 });
                PassiveCatalogFixture.LoadWeaponTiers(Database.CatalogPath, new HashSet<int> { 10678 });
                PassiveCatalogFixture.LoadMissileTiers(Database.CatalogPath, new HashSet<int> { 499, 210 });
                // This saved seed selects one of each specialist in the first four home slots.
                using var setup = new WorldDbContext(Database.Path);
                setup.Database.ExecuteSqlRaw("UPDATE WorldCapsuleerProvisioning SET WorldSeed=45");
                var imported = Enumerable
                    .Range(1, 32)
                    .Select(index => TrafficSnapshotTests.Profile(900000100 + index, $"Snapshot Pilot {index}") with { KillId = index })
                    .ToArray();
                TrafficSnapshotTests.Install(Database, imported);
            }
            if (supportCatalog)
            {
                PassiveCatalogFixture.LoadPlayerEwar(Database.CatalogPath);
            }
            if (thermalCatalog)
            {
                // Only Thermodynamics and its prerequisites are missing from this combat fixture.
                const int scienceSkill = 3402;
                PassiveCatalogFixture.LoadOverload(
                    Database.CatalogPath,
                    new HashSet<int>
                    {
                        Eden.Gameplay.Thermal.OverloadRules.ThermodynamicsSkill,
                        (int)FittingSkill.PowerGridManagement,
                        (int)FittingSkill.CapacitorManagement,
                        scienceSkill,
                    }
                );
            }
            using (var catalog = new CatalogDbContext(Database.CatalogPath, writable: true))
            {
                if (targetLimit is { } limit)
                {
                    int targetAttribute = (int)DogmaAttributeId.MaxLockedTargets;
                    catalog.Database.ExecuteSql(
                        $"UPDATE TypeAttribute SET Value={limit} WHERE ItemTypeId=603 AND AttributeId={targetAttribute}"
                    );
                }
                if (regional)
                {
                    const int rivalConstellation = 20009999;
                    int region = catalog.RequireSystem(Home).RegionId;
                    catalog.Database.ExecuteSql(
                        $"INSERT INTO Constellation VALUES ({rivalConstellation},'Rival test constellation',{region})"
                    );
                    catalog.Database.ExecuteSql($"UPDATE SolarSystem SET ConstellationId={rivalConstellation} WHERE Id={Away}");
                    if (splitRegions)
                    {
                        int remoteRegion = region + 1000;
                        catalog.Database.ExecuteSql($"INSERT INTO Region VALUES ({remoteRegion},'Remote test region')");
                        catalog.Database.ExecuteSql($"UPDATE Constellation SET RegionId={remoteRegion} WHERE Id={rivalConstellation}");
                    }
                }
                foreach (int system in new[] { Home, Away })
                {
                    string key = system.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    catalog.Database.ExecuteSql(
                        $"UPDATE SourceRecord SET Data='{{\"security\":-0.2}}' WHERE Dataset='client/solarsystemcontent' AND Key={key}"
                    );
                    catalog.Database.ExecuteSql(
                        $"INSERT OR REPLACE INTO SourceRecord VALUES('client/systems',{key},'{{\"securityStatus\":-0.2}}')"
                    );
                }
            }
            var store = new WorldConfigurationStore(Database, _world.Clock);
            var saved = store.Read();
            var settings = saved.Active.Draft.Settings;
            settings = settings with
            {
                Capsuleers = settings.Capsuleers with
                {
                    TrafficMultiplier = 10,
                    RoamingGangSize = gangSize,
                    GateVisitors = 0,
                    Miners = false,
                    GeneratedCombatPercent = 100,
                    InterdictorPercent = 0,
                    RoamPercent = 100,
                    GateCamps = false,
                    BeltRatting = false,
                    AnomalyRatting = false,
                },
            };
            store.Save(saved.Generation, saved.Active.Draft with { Settings = settings }, "gang fixture");
            WorldInitialization.Initialize(Database, _world.Clock);
            using (var provisioner = new CapsuleerProvisioner(Database, _world.Clock))
            {
                if (gangFrigates)
                {
                    Host = ServerServices.Build(Database, _world.Clock, liveWorld: true);
                    var synchronization = Host.GetRequiredService<IWorldSynchronization>();
                    CapsuleerProvisioningTurn? turn = null;
                    for (int batch = 0; batch < 32 && turn?.HasMoreWork != false; batch++)
                    {
                        turn = provisioner.PrepareBackgroundSystem(Home, synchronization, () => true, CancellationToken.None);
                        Assert.NotNull(turn);
                    }
                    Assert.False(turn!.HasMoreWork);
                    if (regional)
                    {
                        using (var action = Live.Economy.Begin())
                        {
                            action.Set<SavedCapsuleerProvisioning>().Single().WorldSeed = 2;
                            action.Commit();
                        }
                        provisioner.EnsureSystem(Away);
                    }
                }
                else
                {
                    provisioner.EnsureSystem(Home);
                    if (regional)
                    {
                        // Both one-gate branches need a nonempty cast for this conflict scenario.
                        using var setup = new WorldDbContext(Database.Path);
                        setup.Database.ExecuteSqlRaw("UPDATE WorldCapsuleerProvisioning SET WorldSeed=2");
                        provisioner.EnsureSystem(Away);
                    }
                    Host = ServerServices.Build(Database, _world.Clock, liveWorld: true);
                }
            }
            using (var action = Live.Economy.Begin())
            {
                var roster = action
                    .Set<SavedCapsuleerHauler>()
                    .AsEnumerable()
                    .OrderBy(row => action.Set<SavedCapsuleerAgent>().Find(row.PilotId)!.CommunitySystemId != Home)
                    .ThenBy(row => row.PilotId)
                    .ToArray();
                Assert.True(roster.Length >= 4);
                int corporation = action.Pilots.Find(roster[0].PilotId)!.CorporationId;
                foreach (var row in roster.Take(4))
                {
                    var pilot = action.Pilots.Find(row.PilotId)!;
                    pilot.CorporationId = corporation;
                    var ship = action.Ships.Find(pilot.ActiveShipId)!;
                    if (action.Positions.Find(ship.Id) is { } position)
                    {
                        action.Remove(position);
                    }
                    ship.StationId = HomeStation;
                    row.SourceStationId = HomeStation;
                    row.DestinationStationId = AwayStation;
                    row.RouteSystems = [Home, Away];
                    row.Progress = null;
                }
                foreach (var row in roster.Skip(4))
                {
                    action.Add(new SavedCapsuleerSuspension { PilotId = row.PilotId });
                }
                action.Commit();
            }
            SelectHome();
        }

        internal void Tick()
        {
            _world.Clock.Seconds++;
            Host.GetRequiredService<CapsuleerMaintenance>().Tick();
        }

        internal RoamingGang Form()
        {
            for (int turn = 0; turn < 120; turn++)
            {
                Tick();
                var gang = Live.HaulerCheckpoints.Values.Select(value => value.Work.Gang).FirstOrDefault(value => value is not null);
                if (gang is not null)
                {
                    return gang;
                }
            }
            throw new InvalidOperationException("The fixture could not form its gang.");
        }

        private void SelectHome()
        {
            _presence = Host.GetRequiredService<IHumanPresence>().Select(Human, Home);
            Scheduler.PopulationPrepared(Home);
        }

        internal void Restart()
        {
            _presence?.Dispose();
            Host.Dispose();
            Host = ServerServices.Build(Database, _world.Clock, liveWorld: true);
            SelectHome();
        }

        internal void PauseHuman()
        {
            _presence?.Dispose();
            _presence = null;
        }

        public void Dispose()
        {
            _presence?.Dispose();
            Host.Dispose();
            _world.Dispose();
        }
    }
}
