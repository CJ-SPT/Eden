using Eden.Data.Catalog;
using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.Flight;
using Eden.Protocol.Tests.Sovereignty;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Alliances;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAllianceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FundedRivalsContestAnOwnedHubUnattendedAcrossRestart(bool defend) => ExerciseRivalAttack(defend, rankRivals: false);

    [Fact]
    public Task FundedRivalRaiderChoosesLowerDefenseHubAcrossRestart() => ExerciseRivalAttack(defend: false, rankRivals: true);

    [Fact]
    public Task NeutralRaiderUsesNewFrontierAndReturnsWhenItIsLostAcrossRestart() =>
        ExerciseRivalAttack(defend: false, rankRivals: false, frontierRaid: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UnattendedRaiderRespondsToVisibleInterceptorOnTheHubGrid(bool hostile) =>
        ExerciseRivalAttack(defend: false, rankRivals: false, hostileGrid: hostile);

    private static async Task ExerciseRivalAttack(bool defend, bool rankRivals, bool frontierRaid = false, bool? hostileGrid = null)
    {
        const int home = 30003990;
        const int frontier = RecoveredTerritoryDecoySystem;
        const int target = SovereigntyCaptureFixture.SystemId;
        const int fifthSystem = 31000004;
        const int fuel = 80;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 16, 18, 15, 0, TimeSpan.Zero);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        var configuration = new WorldConfigurationStore(fixture.Database, fixture.Clock);
        var startup = configuration.Read();
        var draft = startup.Active.Draft;
        configuration.Save(
            startup.Generation,
            draft with
            {
                Settings = draft.Settings with
                {
                    Economy = draft.Settings.Economy with
                    {
                        ProgressiveMarkets = false,
                        StandardMarket = false,
                        ChargeServiceCosts = false,
                    },
                    Capsuleers = draft.Settings.Capsuleers with
                    {
                        TrafficMultiplier = 6,
                        AutomaticAlliances = false,
                        RunWithoutPlayers = true,
                        NewSovereigntyClaims = true,
                        RegionalConflicts = true,
                        SituationalAwareness = hostileGrid.HasValue || draft.Settings.Capsuleers.SituationalAwareness,
                    },
                },
            },
            "Rival sovereignty fixture"
        );
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        PassiveCatalogFixture.LoadSovereignty(fixture.Database.CatalogPath);
        PassiveCatalogFixture.LoadUtilityHull(fixture.Database.CatalogPath, CapsuleerAllianceRules.ExpeditionSquall);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            int homeNeighbor = frontierRaid ? HeadquartersSystem : target;
            catalog.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (3732,'Empire Control',257);
                INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3732,275,5);
                DELETE FROM Stargate WHERE SolarSystemId IN ({home},{target});
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                    (55000001,{HeadquartersSystem},16,10,3500,100000000000,0,0),
                    (55000002,{target},16,10,3500,200000000000,0,0),
                    (55000003,{home},16,10,3500,100000000000,0,0),
                    (55000004,{homeNeighbor},16,10,3500,300000000000,0,0);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                    (55000001,{HeadquartersSystem},55000002),(55000002,{target},55000001),
                    (55000003,{home},55000004),(55000004,{homeNeighbor},55000003);
                """
            );
            int nextGateId = 55000005;
            long outwardX = 400_000_000_000;
            int constellation = catalog
                .Set<SolarSystem>()
                .Where(system => system.Id == target)
                .Select(system => system.ConstellationId)
                .Single();
            int[] systems = catalog
                .Set<SolarSystem>()
                .Where(system => system.ConstellationId == constellation && system.Id != home && system.Id != target)
                .OrderBy(system => system.Id)
                .Select(system => system.Id)
                .ToArray();
            foreach (int system in systems)
            {
                int outward = nextGateId++;
                int inward = nextGateId++;
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                        ({outward},{target},16,10,3500,{outwardX},0,0),({inward},{system},16,10,3500,100000000000,0,0);
                    INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                        ({outward},{target},{inward}),({inward},{system},{outward});
                    """
                );
                outwardX += 100_000_000_000;
            }
            if (!defend)
            {
                int outward = nextGateId++;
                int inward = nextGateId;
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                        ({outward},{HeadquartersSystem},16,10,3500,200000000000,0,0),
                        ({inward},{RecoveredTerritoryDecoySystem},16,10,3500,200000000000,0,0);
                    INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                        ({outward},{HeadquartersSystem},{inward}),({inward},{RecoveredTerritoryDecoySystem},{outward});
                    """
                );
            }
        }
        using (CapsuleerProvisioner provisioner = new(fixture.Database, fixture.Clock))
        {
            foreach (int system in new[] { HeadquartersSystem, WingSystem, ThirdSystem, FourthSystem, fifthSystem })
            {
                provisioner.EnsureSystem(system);
            }
        }

        (int Corporation, long Ceo, int Alliance) attackers;
        (int Corporation, long Ceo, int Alliance) defenders;
        long[] attackCouriers;
        long attacker;
        long defender;
        long hubId;
        SovereigntyClaim original;
        SovereigntyClaim homeClaim;
        SovereigntyExpedition underway;
        long reserve = AllianceBillingRules.Amount(CapsuleerAllianceRules.FoundingCorporations);
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var alliances = host.GetRequiredService<AllianceStore>();
            using (var setup = live.Economy.Begin(TransactionParticipants.None))
            using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
            {
                attackers = FoundAlliance(setup, catalog, HeadquartersSystem, "Expedition", "EXPD", ThirdSystem);
                defenders = FoundAlliance(setup, catalog, WingSystem, "Garrison", "GARN", FourthSystem, fifthSystem);
                setup.Set<SavedCorporation>().Find(attackers.Corporation)!.BalanceCents = reserve;
                setup.Set<SavedCorporation>().Find(defenders.Corporation)!.BalanceCents = AllianceBillingRules.Amount(
                    CapsuleerAllianceRules.TargetCorporations
                );

                attackCouriers = FitCouriers(setup, catalog, attackers.Corporation, defend ? 1 : 3);
                attacker = attackCouriers[0];
                defender = Assert.Single(FitCouriers(setup, catalog, defenders.Corporation, 1));
                foreach (var agent in setup.Set<SavedCapsuleerAgent>().ToArray())
                {
                    if (agent.PilotId != attacker && (!defend || agent.PilotId != defender))
                    {
                        setup.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId });
                    }
                }
                AddOwnedHub(setup, catalog, home, attackers.Corporation, attackers.Alliance);
                hubId = AddOwnedHub(setup, catalog, target, defenders.Corporation, defenders.Alliance);
                if (rankRivals)
                {
                    AddOwnedHub(setup, catalog, RecoveredTerritoryDecoySystem, defenders.Corporation, defenders.Alliance);
                    setup.Add(
                        new SavedSovereigntyActivity
                        {
                            SystemId = RecoveredTerritoryDecoySystem,
                            MilitaryPoints = SovereigntyDevelopmentRules.MaximumPoints,
                            UpdatedAt = fixture.Clock.Now,
                        }
                    );
                }
                setup.Commit();
            }
            var entosis = host.GetRequiredService<IPilotEntosis>();
            var conflict = entosis.Hub(target)!.Conflict!;
            Assert.True(SovereigntyWarfareRules.Open(conflict.Window, fixture.Clock.Now));
            Assert.Equal(1d, conflict.Capture.Value);
            original = host.GetRequiredService<ISovereignty>().Read(target)!;
            homeClaim = host.GetRequiredService<ISovereignty>().Read(home)!;
            var social = host.GetRequiredService<IAllianceSocial>();
            social.SetStanding(attackers.Ceo, [defenders.Alliance], 10);
            social.SetStanding(defenders.Ceo, [attackers.Alliance], 10);
            StartRoutines(host);

            Tick(host, 45);
            AssertIdle();
            using (var funding = live.Economy.Begin(TransactionParticipants.None))
            {
                funding.Set<SavedCorporation>().Find(attackers.Corporation)!.BalanceCents = reserve - 1;
                funding.Commit();
            }
            social.SetStanding(attackers.Ceo, [defenders.Alliance], -10);
            Tick(host, 45);
            AssertIdle();
            if (!defend && !rankRivals && hostileGrid is null)
            {
                social.SetStanding(attackers.Ceo, [defenders.Alliance], 0);
                Assert.Equal(0d, social.Standing(attackers.Alliance, defenders.Alliance));
            }
            if (rankRivals)
            {
                var decoy = entosis.Hub(RecoveredTerritoryDecoySystem)!.Conflict!;
                Assert.True(SovereigntyWarfareRules.Open(decoy.Window, fixture.Clock.Now));
                Assert.Equal(1d, decoy.Capture.Value);
                Assert.True(decoy.DefenseMultiplier > conflict.DefenseMultiplier);
                AssertComparableRivalHubs(Fuel(live, attacker));
            }
            using (var funding = live.Economy.Begin(TransactionParticipants.None))
            {
                funding.Set<SavedCorporation>().Find(attackers.Corporation)!.BalanceCents = reserve;
                funding.Commit();
            }
            if (frontierRaid)
            {
                using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
                Assert.DoesNotContain(target, CapsuleerTrafficRoutes.Neighbours(catalog, home));
                Assert.Contains(target, CapsuleerTrafficRoutes.Neighbours(catalog, frontier));
                Assert.Contains(target, CapsuleerAlliances.NearbyClaimSites(catalog, HeadquartersSystem));
                live.Economy.Read(world =>
                {
                    var corporation = world.Set<SavedCorporation>().Find(attackers.Corporation)!;
                    Assert.Equal(reserve, corporation.BalanceCents);
                    var rival = world.Set<SavedSovereigntyClaim>().Find(target)!;
                    Assert.Equal(hubId, Assert.Single(PilotEntosisStore.AttackTargets(world, rival, fixture.Clock.Now)).Id);
                    var route = CapsuleerTrafficRoutes.ToClaimSystem(
                        catalog,
                        corporation.StationId,
                        target,
                        HullClass.Hauler,
                        new HashSet<int>(),
                        policy: world.Settings.Capsuleers
                    );
                    Assert.Equal(new[] { HeadquartersSystem, target }, Assert.IsType<CapsuleerTrafficRoute>(route).Systems);
                    return true;
                });
                var owned = host.GetRequiredService<ISovereignty>();
                Assert.Equal(homeClaim, Assert.Single(owned.List(attackers.Alliance)));
                Tick(host, 45);
                AssertIdle();

                social.SetStanding(attackers.Ceo, [defenders.Alliance], 10);
                using (var acquisition = live.Economy.Begin(TransactionParticipants.None))
                {
                    AddOwnedHub(acquisition, catalog, frontier, attackers.Corporation, attackers.Alliance);
                    acquisition.Commit();
                }
                Assert.True(owned.Read(frontier)!.ClaimedAt > homeClaim.ClaimedAt);
                Tick(host, 45);
                AssertIdle();
                social.SetStanding(attackers.Ceo, [defenders.Alliance], 0);
                Assert.Equal(0d, social.Standing(attackers.Alliance, defenders.Alliance));
            }
            Tick(host, 180, () => Expedition(live, attacker) is { Duty: SovereigntyDuty.Attack });
            underway = Assert.IsType<SovereigntyExpedition>(Expedition(live, attacker));
            Assert.Equal(SovereigntyDuty.Attack, underway.Duty);
            Assert.Equal(new SovereigntyAttackObjective(target, hubId, defenders.Alliance), underway.Attack);
            Assert.Equal(frontierRaid ? frontier : home, underway.HomeSystemId);
            Assert.Equal(hubId, underway.HubId);
            Assert.Empty(entosis.Cycles(attacker));
            await live.FlushAsync();

            void AssertIdle()
            {
                Assert.Null(Expedition(live, attacker));
                Assert.Null(Expedition(live, defender));
                Assert.Empty(entosis.Cycles(attacker));
                Assert.Empty(entosis.Cycles(defender));
                Assert.Equal(original, host.GetRequiredService<ISovereignty>().Read(target));
                Assert.Equal(fuel, Fuel(live, attacker));
                Assert.Equal(fuel, Fuel(live, defender));
            }

            void AssertComparableRivalHubs(int carriedFuel)
            {
                using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
                live.Economy.Read(world =>
                {
                    int[] ordered = CapsuleerAlliances
                        .NearbyClaimSites(catalog, HeadquartersSystem)
                        .Where(system => system is RecoveredTerritoryDecoySystem or target)
                        .ToArray();
                    Assert.Equal(new[] { RecoveredTerritoryDecoySystem, target }, ordered);
                    int headquarters = world.Set<SavedCorporation>().Find(attackers.Corporation)!.StationId;
                    var profile = EntosisCatalog.Module(catalog.FittingAttributes(CapsuleerAllianceRules.ExpeditionLink), 1);
                    long[] durations = ordered
                        .Select(system =>
                        {
                            var claim = world.Set<SavedSovereigntyClaim>().Find(system)!;
                            var hub = Assert.Single(PilotEntosisStore.AttackTargets(world, claim, fixture.Clock.Now));
                            Assert.Equal(new SovereigntyEntosisTarget(system, claim.HubId, false), hub);
                            var route = CapsuleerTrafficRoutes.ToClaimSystem(
                                catalog,
                                headquarters,
                                system,
                                HullClass.Hauler,
                                new HashSet<int>(),
                                policy: world.Settings.Capsuleers
                            );
                            Assert.Equal(new[] { HeadquartersSystem, system }, Assert.IsType<CapsuleerTrafficRoute>(route).Systems);
                            long leadTime = TimeSpan.FromMilliseconds(profile.Duration).Ticks + 2 * TimeSpan.TicksPerMinute;
                            Assert.True(PilotEntosisStore.AttackCanWarmUp(world, system, hub.Id, fixture.Clock.Now, leadTime));
                            long duration = PilotEntosisStore.AttackDuration(world, catalog, claim, hub);
                            Assert.True(carriedFuel >= CapsuleerHauler.RequiredExpeditionCycles(duration, profile) * profile.FuelQuantity);
                            return duration;
                        })
                        .ToArray();
                    Assert.True(durations[0] > durations[1]);
                    return true;
                });
            }

            (int Corporation, long Ceo, int Alliance) FoundAlliance(
                EconomyTransaction world,
                CatalogDbContext catalog,
                int system,
                string name,
                string ticker,
                params int[] partners
            )
            {
                var (corporation, ceo) = Charter(world, catalog, system, $"{name} Works", ticker + "C", fixture.Clock.Now);
                world.Set<PilotWallet>().Find(ceo)!.Cents = AllianceRules.CreationFeeCents;
                world.Add(
                    new PilotSkill
                    {
                        PilotId = ceo,
                        TypeId = AllianceRules.EmpireControlSkill,
                        Points = TrainedSkill.PointsForLevel(5, AllianceRules.RequiredEmpireControlLevel),
                    }
                );
                var alliance = alliances.Create(world, catalog, ceo, new(name, ticker, "", ""), fixture.Clock.Now).Alliance;
                for (int index = 0; index < partners.Length; index++)
                {
                    int partnerSystem = partners[index];
                    long partnerFounder = world
                        .Filter<SavedCapsuleerAgent>(agent => agent.CommunitySystemId == partnerSystem)
                        .OrderBy(agent => agent.PilotId)
                        .First()
                        .PilotId;
                    var partner = CharterCorporation(
                        world,
                        catalog,
                        partnerSystem,
                        $"{name} Wing {index + 1}",
                        $"{ticker}{index + 1}",
                        fixture.Clock.Now,
                        [partnerFounder]
                    );
                    world.Add(
                        new SavedAllianceMember
                        {
                            CorporationId = partner.Id,
                            AllianceId = alliance.Id,
                            JoinedAt = fixture.Clock.Now,
                        }
                    );
                }
                return (corporation, ceo, alliance.Id);
            }
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        var warfare = reopened.GetRequiredService<IPilotEntosis>();
        var claims = reopened.GetRequiredService<ISovereignty>();
        var checkpoint = restored.Economy.Read(world =>
        {
            var corporation = world.Set<SavedCorporation>().Find(attackers.Corporation)!;
            Assert.True(CapsuleerAlliances.ExpeditionActive(world, corporation));
            var courier = world.Filter<SavedCapsuleerHauler>(row => row.PilotId == attacker).Single();
            return HaulerCheckpoint.Read(courier.Progress);
        });
        Assert.Equal(underway, checkpoint?.SovereigntyExpedition);
        StartRoutines(reopened);
        Assert.Equal(original, claims.Read(target));
        if (hostileGrid is bool hostile)
        {
            var (contact, hubPosition) = PublishInterceptor(hostile);
            var space = reopened.GetRequiredService<SharedSpace>();
            Assert.Contains(
                space.Observe(target, checkpoint!.ShipId, hubPosition, 0),
                sighting => sighting.ShipId == contact.ShipId && sighting.Hull == HullClass.Interceptor
            );
            if (hostile)
            {
                var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
                Tick(reopened, 600, () => Expedition(restored, attacker) is { Returning: true });
                var retreat = Assert.IsType<HaulerObservation>(scheduler.Hauler(attacker));
                Assert.Equal(target, retreat.Pilot.SystemId);
                Assert.Contains(
                    retreat.Work.Routine!.Threats,
                    threat => threat.SystemId == target && threat.ExpiresAt > retreat.Work.ActiveTicks
                );
                Assert.Empty(warfare.Cycles(attacker));
                Assert.Equal(fuel, Fuel(restored, attacker));

                space.Leave(contact.ShipId);
                WaitForRaidReturn(scheduler);
                Tick(reopened, 60);
                var settled = Assert.IsType<HaulerObservation>(scheduler.Hauler(attacker));
                Assert.Null(settled.Work.SovereigntyExpedition);
                Assert.Contains(
                    settled.Work.Routine!.Threats,
                    threat => threat.SystemId == target && threat.ExpiresAt > settled.Work.ActiveTicks
                );
                Assert.Empty(warfare.Cycles(attacker));
                Assert.Equal(fuel, Fuel(restored, attacker));
                Assert.Equal(original, claims.Read(target));
                Assert.Equal(1d, warfare.Hub(target)!.Conflict!.Capture.Value);
                return;
            }
        }
        Tick(reopened, 600, () => warfare.Cycles(attacker).Count > 0);
        var paid = Assert.Single(warfare.Cycles(attacker));
        Assert.Equal((hubId, underway.ModuleId), (paid.HubId, paid.ModuleId));
        Assert.Equal(paid.EndsAt, paid.WarmupUntil);
        Assert.Equal(fuel - 1, Fuel(restored, attacker));
        Assert.Equal(1d, warfare.Hub(target)!.Conflict!.Capture.Value);
        if (hostileGrid == false)
        {
            Assert.Equal(target, restored.Economy.Read(world => world.Set<SavedEntosisCycle>().Find(paid.ModuleId)!.SystemId));
            var expedition = Assert.IsType<SovereigntyExpedition>(Expedition(restored, attacker));
            Assert.Equal(underway.Attack, expedition.Attack);
            Assert.False(expedition.Returning);

            var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
            scheduler.ObserveAggression(paid.ShipId);
            Tick(reopened, 20, () => Expedition(restored, attacker) is { Returning: true });
            int beforeDeadline = checked((int)((paid.EndsAt - fixture.Clock.Now - TimeSpan.TicksPerSecond) / TimeSpan.TicksPerSecond));
            Assert.True(beforeDeadline > 0);
            Tick(reopened, beforeDeadline);
            var holding = Assert.IsType<HaulerObservation>(scheduler.Hauler(attacker));
            Assert.True(fixture.Clock.Now < paid.EndsAt);
            Assert.Equal(HaulerActivity.Waiting, holding.Work.Activity);
            Assert.Null(holding.Work.Navigation.Warp);
            Assert.Null(holding.Work.Navigation.WarpDestination);
            Assert.Equal(paid.EndsAt, Assert.Single(warfare.Cycles(attacker)).EndsAt);
            Assert.Equal(fuel - 1, Fuel(restored, attacker));
            WaitForRaidReturn(scheduler);
            Assert.True(fixture.Clock.Now >= paid.EndsAt);
            Assert.Empty(warfare.Cycles(attacker));
            Assert.Equal(fuel - 1, Fuel(restored, attacker));
            Assert.Equal(original, claims.Read(target));
            Assert.Equal(1d, warfare.Hub(target)!.Conflict!.Capture.Value);
            return;
        }
        if (frontierRaid)
        {
            var border = Assert.IsType<SovereigntyClaim>(claims.Read(frontier));
            Assert.Equal(attackers.Alliance, border.AllianceId);
            using (var loss = restored.Economy.Begin(TransactionParticipants.None))
            {
                // Seed the settled loss of this claim without changing either alliance or the target hub.
                var structure = loss.Set<SavedStructure>().Find(border.HubId)!;
                structure.State = StructureState.Destroyed;
                structure.StateStartedAt = fixture.Clock.Now;
                loss.Remove(loss.Set<SavedSovereigntyClaim>().Find(frontier)!);
                loss.Remove(loss.Set<SavedSovereigntyHub>().Find(frontier)!);
                if (loss.Set<SavedSovereigntyConflict>().Find(frontier) is { } conflict)
                {
                    loss.Remove(conflict);
                }
                loss.Commit();
            }
            Assert.Null(claims.Read(frontier));
            Tick(reopened, 900, () => Expedition(restored, attacker) is { Returning: true });
            var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
            int headquarters = restored.Economy.Read(world => world.Set<SavedCorporation>().Find(attackers.Corporation)!.StationId);
            Tick(
                reopened,
                1200,
                () => scheduler.Hauler(attacker) is { Pilot.Position: null, Pilot.DockedLocationId: var station } && station == headquarters
            );
            Tick(reopened, 180, () => Expedition(restored, attacker) is null);
            Assert.Empty(warfare.Cycles(attacker));
            Assert.Equal(fuel - 1, Fuel(restored, attacker));
            var untouched = warfare.Hub(target)!.Conflict!;
            Assert.Equal((SovereigntyPhase.Secure, 1d), (untouched.Phase, untouched.Capture.Value));
            Assert.Equal(original, claims.Read(target));
            Assert.Equal(homeClaim, Assert.Single(claims.List(attackers.Alliance)));
            AssertUnattended(reopened);
            return;
        }
        if (defend)
        {
            Tick(reopened, 1600, () => warfare.Hub(target)!.Conflict!.Capture.Blocked);
            var contested = warfare.Hub(target)!.Conflict!;
            Assert.Equal(SovereigntyPhase.Secure, contested.Phase);
            Assert.True(contested.Capture.Blocked);
            Assert.Equal(0d, contested.Capture.Rate);
            Assert.True(contested.Capture.Value is > 0 and < 1);
            Assert.Equal(SovereigntyDuty.Defend, Expedition(restored, defender)!.Duty);
            Assert.Equal(hubId, Assert.Single(warfare.Cycles(defender)).HubId);
            Assert.True(Fuel(restored, defender) < fuel);
            Assert.True(Fuel(restored, attacker) < fuel - 1);
        }
        else
        {
            Tick(reopened, 4000, () => warfare.Hub(target)!.Conflict!.Phase == SovereigntyPhase.Reinforced);
            var reinforced = warfare.Hub(target)!.Conflict!;
            Assert.Equal(SovereigntyPhase.Reinforced, reinforced.Phase);
            if (rankRivals)
            {
                var decoy = warfare.Hub(RecoveredTerritoryDecoySystem)!.Conflict!;
                Assert.Equal((SovereigntyPhase.Secure, 1d), (decoy.Phase, decoy.Capture.Value));
                Assert.Equal(defenders.Alliance, claims.Read(RecoveredTerritoryDecoySystem)!.AllianceId);
                Assert.Equal(original, claims.Read(target));
                Assert.Equal(homeClaim, claims.Read(home));
                Assert.True(Fuel(restored, attacker) < fuel - 1);
                Assert.Equal(
                    reserve,
                    restored.Economy.Read(world => world.Set<SavedCorporation>().Find(attackers.Corporation)!.BalanceCents)
                );
                AssertUnattended(reopened);
                return;
            }
            long campaignAt = Assert.IsType<long>(reinforced.CampaignAt);
            Assert.Empty(CampaignNodes(restored));
            fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(campaignAt - fixture.Clock.Now));
            warfare.Advance(target, fixture.Clock.Now);
            Assert.Equal(SovereigntyPhase.Campaign, warfare.Hub(target)!.Conflict!.Phase);
            var nodes = CampaignNodes(restored);
            Assert.Equal(SovereigntyWarfareRules.InitialNodes, nodes.Length);
            Assert.Equal(original, claims.Read(target));
            await restored.FlushAsync();
            reopened.Dispose();

            using var campaignHost = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
            var campaignWorld = campaignHost.GetRequiredService<LiveWorld>();
            var campaignWar = campaignHost.GetRequiredService<IPilotEntosis>();
            var campaignClaims = campaignHost.GetRequiredService<ISovereignty>();
            StartRoutines(campaignHost);
            Assert.Equal(nodes.Select(node => node.Id), CampaignNodes(campaignWorld).Select(node => node.Id));
            Assert.Equal(SovereigntyWarfareRules.InitialDefenderPoints, campaignWar.Hub(target)!.Conflict!.DefenderPoints);
            var nodeIds = nodes.Select(node => node.Id).ToHashSet();
            int beforeNodeFuel = Fuel(campaignWorld, attacker);
            Tick(campaignHost, 1200, () => campaignWar.Cycles(attacker).Any(cycle => nodeIds.Contains(cycle.HubId)), campaignWar);
            var nodeCycle = Assert.Single(campaignWar.Cycles(attacker), cycle => nodeIds.Contains(cycle.HubId));
            var nodeExpedition = Assert.IsType<SovereigntyExpedition>(Expedition(campaignWorld, attacker));
            Assert.Equal(SovereigntyDuty.Attack, nodeExpedition.Duty);
            Assert.Equal(underway.Attack, nodeExpedition.Attack);
            Assert.Equal(nodeExpedition.HubId, nodeCycle.HubId);
            Assert.Equal(beforeNodeFuel - 1, Fuel(campaignWorld, attacker));
            Tick(
                campaignHost,
                1800,
                () => campaignWar.Hub(target)!.Conflict!.DefenderPoints < SovereigntyWarfareRules.InitialDefenderPoints,
                campaignWar
            );
            var campaign = campaignWar.Hub(target)!.Conflict!;
            Assert.Equal(SovereigntyPhase.Campaign, campaign.Phase);
            Assert.Equal(SovereigntyWarfareRules.InitialDefenderPoints - SovereigntyWarfareRules.NodePoints, campaign.DefenderPoints);
            Assert.DoesNotContain(CampaignNodes(campaignWorld), node => node.Id == nodeCycle.HubId);
            Assert.Equal(original, campaignClaims.Read(target));
            var credit = Assert.Single(CampaignCredits(campaignWorld, hubId));
            Assert.Equal((target, attackers.Alliance), (credit.SystemId, credit.AllianceId));
            Assert.Null(credit.VictoryAt);

            var beforeFleetFuel = attackCouriers.ToDictionary(pilot => pilot, pilot => Fuel(campaignWorld, pilot));
            using (var mobilize = campaignWorld.Economy.Begin(TransactionParticipants.None))
            {
                foreach (long pilot in attackCouriers.Skip(1))
                {
                    mobilize.Remove(mobilize.Set<SavedCapsuleerSuspension>().Find(pilot)!);
                }
                mobilize.Commit();
            }
            campaignHost.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(HeadquartersSystem);

            EntosisCycle[] PaidNodeCycles() =>
                attackCouriers
                    .SelectMany(campaignWar.Cycles)
                    .Where(cycle => !cycle.Stopping && cycle.HubId != hubId && cycle.EndsAt > fixture.Clock.Now)
                    .ToArray();

            Tick(campaignHost, 1800, () => PaidNodeCycles().Length >= 2, campaignWar, attackCouriers);
            var overlap = PaidNodeCycles();
            Assert.Equal(overlap.Length, overlap.Select(cycle => cycle.HubId).Distinct().Count());
            foreach (var cycle in overlap)
            {
                Assert.Equal(underway.Attack, Expedition(campaignWorld, cycle.PilotId)!.Attack);
                Assert.True(Fuel(campaignWorld, cycle.PilotId) < beforeFleetFuel[cycle.PilotId]);
            }

            HashSet<long> paidNodes = [nodeCycle.HubId];
            Tick(campaignHost, 3 * 60 * 60, Victory, campaignWar, attackCouriers);
            int requiredNodes =
                (SovereigntyWarfareRules.InitialDefenderPoints + SovereigntyWarfareRules.NodePoints - 1)
                / SovereigntyWarfareRules.NodePoints;
            Assert.True(paidNodes.Count >= requiredNodes);
            Assert.Null(campaignClaims.Read(target));
            Assert.Null(campaignWar.Hub(target));
            Assert.Equal(attackers.Alliance, campaignClaims.Read(home)!.AllianceId);
            Assert.Empty(CampaignNodes(campaignWorld));
            Assert.Equal(StructureState.Destroyed, campaignWorld.Economy.Read(world => world.Set<SavedStructure>().Find(hubId)!.State));
            Assert.InRange(attackCouriers.Sum(pilot => Fuel(campaignWorld, pilot)), 1, beforeFleetFuel.Values.Sum() - requiredNodes + 1);
            var victory = Assert.Single(CampaignCredits(campaignWorld, hubId));
            Assert.Equal((target, attackers.Alliance), (victory.SystemId, victory.AllianceId));
            long wonAt = Assert.IsType<long>(victory.VictoryAt);
            AssertUnattended(campaignHost);
            await ExerciseRecoveredTerritory(
                fixture,
                campaignHost,
                attackCouriers,
                homeClaim,
                hubId,
                wonAt,
                (host, seconds, ready) => Tick(host, seconds, ready, paidPilots: attackCouriers)
            );
            return;

            bool Victory()
            {
                foreach (var cycle in PaidNodeCycles())
                {
                    paidNodes.Add(cycle.HubId);
                }
                return campaignWar.Hub(target) is null;
            }
        }
        Assert.Equal(original, claims.Read(target));
        AssertUnattended(reopened);

        void WaitForRaidReturn(CapsuleerScheduler scheduler)
        {
            int headquarters = restored.Economy.Read(world => world.Set<SavedCorporation>().Find(attackers.Corporation)!.StationId);
            Tick(
                reopened,
                1200,
                () => scheduler.Hauler(attacker) is { Pilot.Position: null, Pilot.DockedLocationId: var station } && station == headquarters
            );
            Tick(reopened, 1000, () => Expedition(restored, attacker) is null);
        }

        (DockedPilot Contact, SpacePosition Hub) PublishInterceptor(bool hostile)
        {
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using var placement = restored.Economy.Begin(new(pilots: [Observer]));
            var hub = placement.Set<SavedStructure>().Find(hubId)!;
            SpacePosition hubPosition = new(hub.X, hub.Y, hub.Z);
            double hubRadius = catalog.FittingType(hub.TypeId)!.Radius ?? 0;
            var pilot = placement.Pilots.Find(Observer)!;
            pilot.CorporationId = hostile ? defenders.Corporation : attackers.Corporation;
            pilot.ActiveShip.TypeId = (int)GangFrigate.Crow;
            var position = hubPosition + new SpacePosition(hubRadius + 5_000, 8_000, 0);
            SqliteWorld.SetPosition(placement, catalog, pilot.ActiveShipId, position, target, fixture.Clock.Now);
            var ships = reopened.GetRequiredService<SqliteWorld>();
            var contact = ships.FindPilot(placement, pilot.AccountId, pilot.Id)!;
            placement.Commit();
            Assert.Equal((int?)(hostile ? defenders.Alliance : attackers.Alliance), contact.Identity.AllianceId);
            reopened.GetRequiredService<SharedSpace>().Publish(new(PilotContact.From(contact), SpacePosition.Zero, null, 0, null));
            return (contact, hubPosition);
        }

        long[] FitCouriers(EconomyTransaction world, CatalogDbContext catalog, int corporation, int count)
        {
            var couriers = world
                .Set<SavedCapsuleerHauler>()
                .AsEnumerable()
                .Where(hauler => world.Pilots.Find(hauler.PilotId)!.CorporationId == corporation)
                .OrderBy(hauler => hauler.PilotId)
                .Take(count)
                .ToArray();
            Assert.Equal(count, couriers.Length);
            int station = world.Set<SavedCorporation>().Find(corporation)!.StationId;
            foreach (var courier in couriers)
            {
                FitCourier(world, catalog, courier, station);
            }
            return couriers.Select(courier => courier.PilotId).ToArray();
        }

        void FitCourier(EconomyTransaction world, CatalogDbContext catalog, SavedCapsuleerHauler courier, int station)
        {
            long pilotId = courier.PilotId;
            var pilot = world.Pilots.Find(pilotId)!;
            long shipId = pilot.ActiveShipId;
            if (world.Positions.Find(shipId) is { } position)
            {
                world.Remove(position);
            }
            pilot.ActiveShip.StationId = station;
            courier.Progress = null;
            world.RemoveRange(world.Filter<OwnedItem>(item => item.LocationId == shipId).ToArray());
            TrafficFitting.Apply(
                world,
                catalog,
                pilotId,
                shipId,
                TrafficSnapshotTests.Profile() with
                {
                    HullTypeId = CapsuleerAllianceRules.ExpeditionSquall,
                    Equipment = [new(CapsuleerAllianceRules.ExpeditionLink, (int)InventoryFlag.HighSlot0, 1)],
                },
                fixture.Clock.Now
            );
            InventoryPersistence.MergeOutput(
                world,
                catalog,
                pilotId,
                shipId,
                (int)InventoryFlag.Cargo,
                SovereigntyCaptureFixture.Strontium,
                fuel
            );
        }

        long AddOwnedHub(EconomyTransaction world, CatalogDbContext catalog, int system, int corporation, int alliance)
        {
            long id = Math.Max(world.NextItemId(), StructureRules.FirstStructureId + 1 + (system == home ? 0 : 1));
            var profile = EntosisCatalog.Hub(catalog);
            world.Add(
                new SavedStructure
                {
                    Id = id,
                    TypeId = EntosisRules.HubTypeId,
                    CorporationId = corporation,
                    SystemId = system,
                    State = StructureState.ShieldVulnerable,
                    StateStartedAt = fixture.Clock.Now,
                    X = profile.StarDistance,
                }
            );
            world.Add(
                new SavedSovereigntyHub
                {
                    SystemId = system,
                    HubId = id,
                    CaptureDuration = profile.CaptureDuration,
                    UpdatedAt = fixture.Clock.Now,
                }
            );
            world.Add(
                new SavedSovereigntyClaim
                {
                    SystemId = system,
                    HubId = id,
                    CorporationId = corporation,
                    AllianceId = alliance,
                    ClaimedAt = fixture.Clock.Now,
                }
            );
            return id;
        }

        void Tick(
            ServiceProvider host,
            int seconds,
            Func<bool>? ready = null,
            IPilotEntosis? paidCycles = null,
            IReadOnlyList<long>? paidPilots = null
        )
        {
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var live = host.GetRequiredService<LiveWorld>();
            var pilotIds = paidPilots ?? [attacker];
            long deadline = checked(fixture.Clock.Milliseconds + 1000L * seconds);
            while (fixture.Clock.Milliseconds < deadline && ready?.Invoke() != true)
            {
                long step = paidCycles is null ? 1000 : StepToCycleEnd(paidCycles);
                fixture.Clock.Milliseconds = Math.Min(deadline, fixture.Clock.Milliseconds + step);
                maintenance.Tick();
            }
            AssertUnattended(host);
            if (ready is not null && !ready())
            {
                var scheduler = host.GetRequiredService<CapsuleerScheduler>();
                var offense = scheduler.Hauler(attacker);
                var defense = scheduler.Hauler(defender);
                var runtime = scheduler.Read();
                var warfareAtFailure = host.GetRequiredService<IPilotEntosis>();
                var conflict = warfareAtFailure.Hub(target)?.Conflict;
                long? targetId = offense?.Work.SovereigntyExpedition?.HubId;
                var node = CampaignNodes(live).SingleOrDefault(candidate => candidate.Id == targetId);
                var job = live.CapsuleerJobs.GetValueOrDefault(attacker);
                var wing = pilotIds
                    .Skip(1)
                    .Select(pilot =>
                    {
                        var operation = scheduler.Hauler(pilot);
                        var paid = warfareAtFailure.Cycles(pilot);
                        return $"{pilot} in {operation?.Pilot.SystemId} at {operation?.Pilot.Position} "
                            + $"dock {operation?.Pilot.DockedLocationId} {operation?.Work.Activity} "
                            + $"{operation?.Work.SovereigntyExpedition}, "
                            + $"active {operation?.Work.ActiveTicks}, due {operation?.Work.DueTicks}, "
                            + $"job {live.CapsuleerJobs.GetValueOrDefault(pilot)}, cycles [{string.Join(" | ", paid)}]";
                    })
                    .ToArray();
                var cycles = live.Economy.Read(world =>
                    world
                        .Filter<SavedEntosisCycle>(cycle => cycle.PilotId == attacker)
                        .Select(cycle =>
                            $"target {cycle.HubId} system {cycle.SystemId} from {cycle.StartedAt} warm {cycle.WarmupUntil} "
                            + $"until {cycle.EndsAt} repeats {cycle.Repeats} contributing {cycle.Contributing} stopping {cycle.Stopping}"
                        )
                        .ToArray()
                );
                Assert.Fail(
                    $"Timed out at {fixture.Clock.UtcNow} ({fixture.Clock.Now}). "
                        + $"Attacker in {offense?.Pilot.SystemId} {offense?.Work.Activity} {offense?.Work.SovereigntyExpedition}. "
                        + $"Position {offense?.Pilot.Position}, max speed {offense?.Pilot.Flight.MaxSpeed}, fuel {Fuel(live, attacker)}. "
                        + $"Work active {offense?.Work.ActiveTicks} due {offense?.Work.DueTicks}; job {job}. "
                        + $"Cycles [{string.Join(" | ", cycles)}]. "
                        + $"Conflict {conflict?.Phase}, points {conflict?.DefenderPoints}, {conflict?.Capture}. "
                        + $"Node {node?.Id} in {node?.SystemId}, value {node?.Value}, rate {node?.Rate}, "
                        + $"team {node?.ScoringTeam}, blocked {node?.Blocked}, at {node?.UpdatedAt}, pos {node?.X}/{node?.Y}/{node?.Z}. "
                        + $"Wing [{string.Join(" | ", wing)}]. "
                        + $"Defender in {defense?.Pilot.SystemId} {defense?.Work.Activity} {defense?.Work.SovereigntyExpedition}. "
                        + $"Routines scheduled {runtime.ScheduledAgents}, waiting {runtime.WaitingBackgroundStarts}, "
                        + $"jobs {runtime.Jobs}, due {runtime.DueJobs}, completed {runtime.CompletedActions}, "
                        + $"turn {runtime.LastTurn}, max overrun {runtime.MaximumTurnOverrun}, failure {runtime.LastFailure}"
                );
            }

            long StepToCycleEnd(IPilotEntosis warfare)
            {
                long now = fixture.Clock.Now;
                long earliestEnd = long.MaxValue;
                foreach (long pilot in pilotIds)
                {
                    if (
                        live.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work is not { Activity: HaulerActivity.Waiting }
                        || warfare.Cycles(pilot).SingleOrDefault() is not { } cycle
                    )
                    {
                        return 1000;
                    }
                    earliestEnd = Math.Min(earliestEnd, cycle.EndsAt);
                }
                return Math.Max(1000, (earliestEnd - now) / TimeSpan.TicksPerMillisecond);
            }
        }

        static void StartRoutines(ServiceProvider host)
        {
            StartAllianceActivity(host, unattended: true);
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(HeadquartersSystem);
            scheduler.PopulationPrepared(WingSystem);
        }

        static SavedSovereigntyNode[] CampaignNodes(LiveWorld live) =>
            live.Economy.Read(world =>
                world.Filter<SavedSovereigntyNode>(node => node.SourceSystemId == target).OrderBy(node => node.Id).ToArray()
            );

        static SovereigntyExpedition? Expedition(LiveWorld live, long pilot) =>
            live.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work.SovereigntyExpedition;

        static int Fuel(LiveWorld live, long pilot) =>
            live.Economy.Read(world =>
            {
                long ship = world.Pilots.Find(pilot)!.ActiveShipId;
                return world
                    .Filter<OwnedItem>(item => item.LocationId == ship)
                    .Where(item => item.TypeId == SovereigntyCaptureFixture.Strontium && item.FlagId == (int)InventoryFlag.Cargo)
                    .Sum(item => item.Quantity);
            });
    }
}
