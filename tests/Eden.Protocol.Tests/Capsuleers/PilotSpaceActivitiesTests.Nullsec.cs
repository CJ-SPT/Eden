using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Ships;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class PilotSpaceActivitiesTests
{
    [Fact]
    public void DefaultExpeditionDrawsFavorRoamingAndRetainBeltRatting()
    {
        const int departures = 1000;
        CapsuleerPopulationPolicy policy = new();
        var draws = Enumerable.Range(0, departures).Select(sequence => NullsecExpeditionRules.Draw(41, 90000123, sequence)).ToArray();
        int roams = draws.Count(draw => NullsecExpeditionRules.ShouldRoam(draw, policy.RoamPercent));
        int ratting = draws.Count(draw =>
            !NullsecExpeditionRules.ShouldRoam(draw, policy.RoamPercent) && NullsecExpeditionRules.ShouldRat(draw)
        );
        Assert.True(roams > departures / 2);
        Assert.InRange(ratting, 1, departures - roams - 1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IndependentTravelerResumesSeededStationlessNullsecExpeditionOffscreen(bool roaming, bool rattingDraw)
    {
        const int corridor = 30001970;
        const int first = corridor + 1;
        const int second = corridor + 2;
        const int observerSystem = 30000142;
        var firstLaunch = await Launch(null, reopen: true);
        var secondLaunch = await Launch(firstLaunch, reopen: false);
        Assert.NotEqual(firstLaunch.Seed, secondLaunch.Seed);
        Assert.Equal(new[] { first, second }, new[] { firstLaunch.System, secondLaunch.System }.Order());

        async Task<(int System, long Seed)> Launch((int System, long Seed)? previous, bool reopen)
        {
            using CapsuleerHaulerTests.Fixture fixture = new(additionalCatalogData: path =>
                AddNullsecBranches(path, addBelts: rattingDraw)
            );
            long pilot = PrepareDockedRatter(fixture);
            var policy = fixture.Database.Settings.Capsuleers with
            {
                NullsecExpeditions = true,
                BeltRatting = true,
                RoamingPvp = roaming,
                RoamPercent = 100,
                RoamingGangs = false,
                EarnedPvpShips = false,
                SystemAccess = fixture.Database.Settings.Capsuleers.SystemAccess.Add(corridor, CapsuleerRegionAccess.TransitOnly),
            };
            WorldConfigurationStore configuration = new(fixture.Database, fixture.Clock);
            var configurationState = configuration.Read();
            configuration.Save(
                configurationState.Generation,
                configurationState.Active.Draft with
                {
                    Settings = configurationState.Active.Draft.Settings with { Capsuleers = policy },
                },
                "test"
            );

            long worldSeed = 0;
            using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
            {
                for (long seed = 1; seed < 1000 && worldSeed == 0; seed++)
                {
                    ulong draw = NullsecExpeditionRules.Draw(seed, pilot, 0);
                    // The old tie breaker sent odd draws to belts when both activities were available.
                    if (
                        seed == previous?.Seed
                        || NullsecExpeditionRules.ShouldRat(draw) != rattingDraw
                        || roaming && rattingDraw && (draw & 1) == 0
                    )
                    {
                        continue;
                    }
                    var destination = CapsuleerTrafficRoutes.NullsecExpedition(
                        catalog,
                        SystemId,
                        HullClass.Frigate,
                        policy,
                        new HashSet<int>(),
                        rattingDraw,
                        roaming,
                        draw
                    );
                    if (destination is not null && destination.Systems[^1] != previous?.System)
                    {
                        worldSeed = seed;
                    }
                }
            }
            Assert.True(worldSeed > 0);
            WorldInitialization.Initialize(fixture.Database, fixture.Clock);

            int target;
            int[] route;
            using (var outbound = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
            {
                var live = outbound.GetRequiredService<LiveWorld>();
                using (var world = live.Economy.Begin(TransactionParticipants.None))
                {
                    var provisioning = world.Set<SavedCapsuleerProvisioning>().SingleOrDefault();
                    provisioning ??= world.Add(new SavedCapsuleerProvisioning { Id = 1 }).Entity;
                    provisioning.WorldSeed = worldSeed;
                    world.Commit();
                }
                using var human = outbound.GetRequiredService<IHumanPresence>().Select(90000001, observerSystem);
                var entered = AdvanceUntil(outbound, state => state.Pilot.SystemId == corridor);
                var journey = Assert.IsType<TrafficJourney>(entered.Work.Journey);
                Assert.Equal(TrafficJourneyKind.NullsecExpedition, journey.Kind);
                Assert.Equal(roaming ? NullsecExpeditionWork.Roaming : NullsecExpeditionWork.Ratting, journey.NullsecWork);
                route = journey.Systems;
                target = route[^1];
                Assert.Contains(target, new[] { first, second });
                Assert.Equal(new[] { SystemId, corridor, target }, route);
                Assert.Null(entered.Work.Ratting);
                Assert.Null(entered.Pilot.Identity.AllianceId);
                if (!reopen)
                {
                    return (target, worldSeed);
                }
                await live.FlushAsync();
            }

            using var inbound = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
            using var player = inbound.GetRequiredService<IHumanPresence>().Select(90000001, observerSystem);
            var onSite = AdvanceUntil(inbound, state => state.Pilot.SystemId == target && state.Work.Ratting is not null);
            Assert.Equal(route, onSite.Work.Journey!.Systems);
            Assert.Equal(roaming ? PatrolKind.Roam : null, onSite.Work.Ratting?.Patrol?.Kind);
            Assert.False(inbound.GetRequiredService<LiveWorld>().IsDetailedSystem(target));
            var returning = AdvanceUntil(inbound, state => state.Pilot.SystemId == corridor && state.Work.RouteDirection < 0);
            Assert.Equal(route, returning.Work.Journey!.Systems);
            var docked = AdvanceUntil(inbound, state => state.Pilot.Position is null && state.Pilot.StationId == Station);
            Assert.Null(docked.Work.Journey);
            Assert.Null(docked.Work.Ratting);
            Assert.Equal(onSite.Pilot.ShipId, docked.Pilot.ShipId);
            Assert.Equal(0, inbound.GetRequiredService<CapsuleerScheduler>().Read().Failures);
            return (target, worldSeed);

            HaulerObservation AdvanceUntil(IServiceProvider host, Func<HaulerObservation, bool> condition)
            {
                var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
                var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
                HaulerObservation? last = null;
                for (int i = 0; i < 1200; i++)
                {
                    fixture.Clock.Seconds++;
                    maintenance.Tick();
                    last = scheduler.Hauler(pilot)!;
                    if (condition(last))
                    {
                        return last;
                    }
                }
                throw new InvalidOperationException(
                    $"The pilot did not complete its nullsec leg. System {last?.Pilot.SystemId}, activity {last?.Work.Activity}, "
                        + $"journey {last?.Work.Journey?.Kind}, ratting {last?.Work.Ratting?.Phase}, waiting {last?.Work.WaitingReason}."
                );
            }
        }

        static void AddNullsecBranches(string path, bool addBelts)
        {
            using CatalogDbContext catalog = new(path, writable: true);
            for (int system = corridor; system <= second; system++)
            {
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO SolarSystem(Id,Name,ConstellationId)
                    SELECT {system}, {system.ToString()}, ConstellationId FROM SolarSystem WHERE Id={SystemId}
                    """
                );
                catalog.Database.ExecuteSql(
                    $"INSERT INTO SourceRecord VALUES ('client/solarsystemcontent',{system.ToString()},{"""{"security":-0.3}"""})"
                );
            }
            Connect(SystemId, corridor, 50009800);
            Connect(corridor, first, 50009802);
            Connect(corridor, second, 50009804);
            if (!addBelts)
            {
                return;
            }
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES(2,'Celestial');
                INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES(9,'Asteroid Belt',2);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES(15,'Asteroid Belt',9);
                """
            );
            foreach (int system in new[] { first, second })
            {
                long belt = 40009800L + system - first;
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
                    VALUES({belt},{system},15,9,-37717647360,849715200,-55091896320);
                    INSERT INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid',{belt.ToString()},{"""{"1230":39}"""});
                    """
                );
            }

            void Connect(int left, int right, int firstGate)
            {
                int nextGate = firstGate + 1;
                catalog.Database.ExecuteSql(
                    $"""
                    INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                        ({firstGate},{left},16,10,3532,900000000000,-100000000000,-1100000000000),
                        ({nextGate},{right},16,10,3532,800000000000,-100000000000,-1100000000000);
                    INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                        ({firstGate},{left},{nextGate}),({nextGate},{right},{firstGate});
                    """
                );
            }
        }
    }
}
