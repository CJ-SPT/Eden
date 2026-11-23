using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Maps;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerMilitiaPatrolTests
{
    [Theory]
    [InlineData(37, true)]
    [InlineData(61, false)]
    public void CorporationsControlDirectMilitiaEnlistmentAfterTravelToAValidOffice(int seed, bool permitsEnlistment)
    {
        const int perimeter = 30000144;
        var directory = TestDirectory.Create("militia-patrol-direct-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
            var scenario = PatrolScenario.FromSeed(seed);
            PrepareCatalog(database.CatalogPath, scenario, connectTradeHub: true);
            Configure(database, clock);
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            var live = host.GetRequiredService<LiveWorld>();
            StagePilots(live, database, clock, scenario, includeDefender: false, fromTradeHub: true);
            int corporation = EmployPatrolPilot(live, clock);
            var enlistment = host.GetRequiredService<IMilitiaEnlistment>();
            Assert.Null(enlistment.Read(AttackerPilot).Effective);
            if (permitsEnlistment)
            {
                Assert.Equal(new[] { scenario.AttackerFaction }, enlistment.SetAllowedFactions(AttackerPilot, [scenario.AttackerFaction]));
            }
            else
            {
                Assert.Empty(enlistment.AllowedFactions(corporation));
            }

            using var observer = host.GetRequiredService<IHumanPresence>().Select(Human, perimeter);
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            scheduler.PopulationPrepared(Jita);
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            bool sawStagingFlight = false;
            bool dockedAtOffice = false;
            bool onObjective = false;
            for (int turn = 0; turn < MaximumSeconds && !onObjective; turn++)
            {
                clock.Seconds++;
                worker.Tick();
                var observation = scheduler.Hauler(AttackerPilot);
                if (observation?.Work.MilitiaPatrol is { Phase: MilitiaPatrolPhase.Staging } staging)
                {
                    Assert.True(permitsEnlistment);
                    Assert.Equal(scenario.AttackerStation, staging.StagingStationId);
                    Assert.Null(enlistment.Read(AttackerPilot).Effective);
                    sawStagingFlight |= observation.Pilot is { SystemId: Jita, Position: not null };
                    dockedAtOffice |= observation.Pilot.StationId == scenario.AttackerStation && observation.Pilot.Position is null;
                }
                onObjective =
                    observation?.Work.MilitiaPatrol is { Phase: MilitiaPatrolPhase.Holding } patrol
                    && patrol.SystemId == scenario.Frontline;
            }

            Assert.Equal(corporation, live.Economy.Inspect(world => world.Pilots.Find(AttackerPilot)!.CorporationId));
            var affiliation = enlistment.Read(AttackerPilot);
            Assert.Null(affiliation.Corporation);
            if (!permitsEnlistment)
            {
                Assert.False(onObjective);
                Assert.Null(affiliation.Effective);
                return;
            }
            Assert.True(sawStagingFlight && dockedAtOffice && onObjective);
            Assert.Equal(scenario.AttackerFaction, affiliation.Direct);
            var jumps = host.GetRequiredService<IMapStatistics>().History(MapHistoryStatistic.Jumps, 1);
            foreach (int system in scenario.Route)
            {
                Assert.Contains(jumps.Rows, row => row.SystemId == system && row.Counts.Jumps > 0);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static int EmployPatrolPilot(LiveWorld live, TestClock clock)
    {
        const string name = "Local Patrol Services";
        const string ticker = "LPS";
        using var world = live.Economy.Begin(new(pilots: [AttackerPilot]));
        var details = new CorporationDetails(name, ticker, "", "", 0, 0, new(null, null, null, null, null, null, null), false, false);
        var corp = world
            .Add(
                new SavedCorporation
                {
                    Name = name,
                    Ticker = ticker,
                    DetailsJson = JsonSerializer.Serialize(details),
                    CeoId = AttackerPilot,
                    CreatorId = AttackerPilot,
                    StationId = JitaStation,
                    SystemId = Jita,
                    CreatedAt = clock.Now,
                    MemberLimit = 10,
                }
            )
            .Entity;
        world.SaveChanges();
        world.Pilots.Find(AttackerPilot)!.CorporationId = corp.Id;
        world.Commit();
        return corp.Id;
    }
}
