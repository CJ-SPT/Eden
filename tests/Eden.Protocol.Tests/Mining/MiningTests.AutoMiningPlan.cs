using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class MiningTests
{
    [Fact]
    public void AutoMiningSearchAndPlanSaveAreAtomicAndSurviveReopen()
    {
        using Fixture f = new(useTestClock: true);
        AddMiningBelt(f, 40009233, 30000144, new(1_000_000, 0, 0));
        var (context, targeting, mining, _) = f.Client(locked: false);
        var controller = CreateController(f, targeting, mining);
        var routes = f.Db.Services.GetRequiredService<IPilotAutoMiningRoutes>();
        var store = f.Db.Services.GetRequiredService<IPilotAutoMining>();
        var hauling = f.Db.Services.GetRequiredService<IPilotAutoMiningHauling>();
        Assert.Equal(new AutoMiningSystem(30000144, "Perimeter"), Assert.Single(routes.SearchSystems("pErI")));
        Assert.Empty(routes.SearchSystems("Jita"));
        Assert.Empty(routes.SearchSystems("P"));
        Assert.Throws<InvalidDataException>(() => routes.SearchSystems(new string('x', 101)));
        var station = Assert.Single(routes.SearchStations("Jita", 30000144));
        Assert.Equal(60003760, station.Id);
        Assert.Empty(routes.SearchStations("missing station", 30000142));
        var search = Assert.IsType<AutoMiningSystems>(
            Assert.IsType<PilotCallResult.Success>(controller.Call(context, AutoMiningHandler.Service, "SearchSystems", ["peri"])).Reply
        );
        var row = BlueRead.Tuple(Assert.Single(Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(search)))), 2);
        Assert.Equal(30000144L, row[0]);
        Assert.Equal("Perimeter", BlueMarshal.Text(row[1]));

        object?[] args = ["Veldspar", 0L, 0L, false, false, station.Id, 80L, new List<object?> { 30000144L }, 0L];
        var saved = Command(controller, context, "SavePlan", args);
        Assert.Equal(station.Name, saved.UnloadStationName);
        Assert.Equal("Perimeter", Assert.Single(saved.RouteStops).Name);
        foreach (
            var invalid in new object?[][]
            {
                ["Scordite", 0L, 0L, true, true, station.Id, 50L, new List<object?> { 42L }, 0L],
                ["Scordite", 0L, 0L, true, true, 42L, 50L, new List<object?> { 30000144L }, 0L],
                ["Scordite", 0L, 0L, true, true, station.Id, 0L, new List<object?> { 30000144L }, 0L],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(controller.Call(context, AutoMiningHandler.Service, "SavePlan", invalid));
            Assert.Equal("Veldspar", store.ReadFilter(context.Pilot.PilotId).Text);
            Assert.False(store.ReadOptions(context.Pilot.PilotId).Approach);
            Assert.Equal(new(station.Id, 80), hauling.ReadHauling(context.Pilot.PilotId));
            Assert.Equal([30000144], routes.ReadRoute(context.Pilot.PilotId).Systems.ToArray());
        }
        // Global station selection also works when saving from outside the unload system.
        routes.SavePlan(
            context.Pilot.PilotId,
            30000144,
            new(
                store.ReadFilter(context.Pilot.PilotId),
                AutoMiningOptions.Default,
                new(station.Id, 80),
                routes.ReadRoute(context.Pilot.PilotId)
            )
        );
        f.Disconnect(context);
        f.Reopen();
        using var live = new LiveWorld(new(f.Db.Path), f.Clock);
        Assert.Equal(new(station.Id, 80), hauling.ReadHauling(90000001));
        Assert.Equal([30000144], routes.ReadRoute(90000001).Systems.ToArray());
        Assert.Equal("Veldspar", store.ReadFilter(90000001).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutoMiningAvoidsRatsAndEitherMinesNextBeltOrDocks(bool nextBelt)
    {
        using Fixture f = new(useTestClock: true);
        MiningEncounters encounters = new();
        var (context, targeting, mining, module) = f.Client(encounters: encounters);
        var origin = context.Pilot.Position!;
        var destination = origin + new SpacePosition(1_000_000, 0, 0);
        AddMiningBelt(f, 40009232, 30000142, origin);
        if (nextBelt)
        {
            AddMiningBelt(f, 40009233, 30000142, destination);
            using WorldDbContext world = new(f.Db.Path);
            world
                .Set<WorldAsteroid>()
                .Add(
                    new()
                    {
                        Id = Fixture.Asteroid + 1,
                        SystemId = 30000142,
                        BeltId = 40009233,
                        TypeId = 1230,
                        Quantity = 63,
                        X = destination.X + 20_000,
                        Y = destination.Y,
                        Z = destination.Z,
                        Radius = 100,
                    }
                );
            world.SaveChanges();
        }
        var controller = CreateController(f, targeting, mining);
        Command(controller, context, "SavePlan", "Veldspar", 0L, 0L, false, false, 0L, 95L, new List<object?> { 30000142L }, 0L);
        Command(controller, context, "Start");
        for (int step = 0; step < 5 && !mining.IsActive(module); step++)
        {
            Step();
        }
        Assert.True(mining.IsActive(module));
        long unsafeRemaining = f.Remaining();
        encounters.Contacts = [MiningRat(origin)];
        context.Flight.RefreshNpcs();
        controller.Advance(context, sessionTick: true);
        Assert.False(mining.IsActive(module));
        Assert.Contains("Avoiding rats", Command(controller, context, "GetStatus").Status);
        bool approached = false;
        for (int step = 0; step < 600 && Command(controller, context, "GetStatus").Running; step++)
        {
            Step();
            approached |= Command(controller, context, "GetStatus").Status == "Approaching asteroid";
        }
        var status = Command(controller, context, "GetStatus");
        Assert.False(status.Running, status.Status);
        Assert.Equal(unsafeRemaining, f.Remaining());
        if (nextBelt)
        {
            Assert.Contains("Route complete", status.Status);
            Assert.True(approached);
            Assert.Equal(
                63,
                f.Inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Where(item => item.TypeId == 1230).Sum(item => item.Quantity)
            );
        }
        else
        {
            Assert.Contains("Docked to avoid rats", status.Status);
            Assert.Null(context.Pilot.Position);
            Assert.Equal(60003760, context.Pilot.StationId);
        }

        void Step()
        {
            f.Clock.Seconds++;
            context.Flight.Checkpoint();
            context.Flight.RefreshNpcs();
            context.Flight.RefreshAsteroids(f.Db.Services.GetRequiredService<IAsteroidField>().List(context.Pilot.SystemId));
            targeting.Advance(context);
            mining.Advance(context);
            controller.Advance(context, sessionTick: true);
        }
    }

    private static void AddMiningBelt(Fixture f, int id, int system, SpacePosition position)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlInterpolated(
            $"""
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z)
            VALUES ({id},{system},15,9,{position.X},{position.Y},{position.Z})
            """
        );
        catalog.Database.ExecuteSqlInterpolated(
            $"INSERT INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid',{id.ToString()},'{{}}')"
        );
    }

    private static NpcContact MiningRat(SpacePosition position) =>
        new(
            999,
            30380,
            0,
            11,
            0,
            "Rat",
            30000142,
            position,
            1_000_000,
            40,
            220,
            1,
            new(999, ShipHealth.DefenseFrom(new Dictionary<Eden.Gameplay.Dogma.DogmaAttributeId, double>()), new(100, 100, 100))
        );

    private sealed class MiningEncounters : INpcEncounters
    {
        internal NpcContact[] Contacts { get; set; } = [];

        public IReadOnlyList<NpcContact> List(int systemId, long now) => Contacts;

        public bool WasDestroyed(long npcId) => false;
    }

    [Fact]
    public void AutoMiningThreatDetectionIgnoresBenignDeadAndDistantNpcs()
    {
        var origin = new SpacePosition(0, 0, 0);
        var rat = MiningRat(origin);
        Assert.True(AutoMiningSafetyRules.Threat(rat, 1, 30000142, origin));
        Assert.False(AutoMiningSafetyRules.Threat(rat with { Role = NpcRole.Navy }, 1, 30000142, origin));
        Assert.False(AutoMiningSafetyRules.Threat(rat with { SystemId = 30000144 }, 1, 30000142, origin));
        Assert.False(AutoMiningSafetyRules.Threat(rat with { Position = new(250_001, 0, 0) }, 1, 30000142, origin));
        Assert.False(AutoMiningSafetyRules.Threat(rat with { Health = rat.Health with { Remaining = new(0, 0, 0) } }, 1, 30000142, origin));
        var hostileGuard = rat with { Role = NpcRole.Navy, Navigation = new(origin, 1, 1, 1000) };
        Assert.True(AutoMiningSafetyRules.Threat(hostileGuard, 1, 30000142, origin));
    }
}
