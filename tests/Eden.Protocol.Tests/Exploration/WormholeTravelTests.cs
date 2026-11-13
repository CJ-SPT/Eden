using System.Text.Json;
using Eden.Data.World.Mining;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class WormholeTravelTests
{
    private const int Known = 30002813;
    private const int Remote = 31000356;
    private const int SystemClass = 2;
    private const int Portal = 30583;

    [Fact]
    public void ProbedWormholeSupportsRoundTripHarvestAndPersistentPolarization()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadMining(f.CatalogPath);
        PassiveCatalogFixture.LoadProbes(f.CatalogPath);
        AddRoute(f);
        long scoop = f.Fit(25266);
        long launcher = f.Fit(17938);
        long probeStack = f.Grant(30013, 8);
        f.Train(3402, 5);
        f.Db.Services.GetRequiredService<IPilotAmmunition>().Load(f.PilotId, f.ShipId, [launcher], [probeStack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 10000;
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var travel = f.Db.Services.GetRequiredService<IWormholeTravel>();
        var sites = f.Db.Services.GetRequiredService<ICombatAnomalies>();
        var entrance = Assert.Single(sites.Signatures(Known, f.Clock.Now), s => s.Wormhole);
        var source = Assert.Single(entrance.Objects);
        Assert.Equal((Portal, Remote), (source.TypeId, source.Wormhole!.DestinationSystem));
        var exit = Assert.Single(Assert.Single(travel.Signatures(Remote)).Objects);
        var result = ProbeScanningWire.Result(new(entrance, 1, source.Position, 0));
        var fields = Assert.IsType<Dictionary<string, object?>>(result.State);
        Assert.Equal(WormholeRules.StrengthAttribute, fields["strengthAttributeID"]);
        Assert.Null(fields["dungeonID"]);
        f.Db.Store.SetPosition(f.ShipId, source.Position, Known);
        var probes = f.Db.Services.GetRequiredService<IPilotProbeScanning>();
        var launched = probes.Launch(f.PilotId, launcher, 8, f.Clock.Now).Probes;
        var formation = ProbeScanningRuleTests
            .Formation(entrance.Position, step: 1)
            .Take(launched.Count)
            .Select((p, index) => p.Placement with { Id = launched[index].Id })
            .ToArray();
        var scan = probes.Begin(f.PilotId, formation, f.Clock.Now);
        f.Clock.Milliseconds += (scan.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        Assert.Equal(1, Assert.Single(probes.Complete(f.PilotId, scan, f.Clock.Now), r => r.Site.Id == entrance.Id).Certainty);
        Assert.True(probes.Resolved(f.PilotId, entrance.Id, Known, f.Clock.Now));
        Assert.False(probes.Resolved(f.PilotId, entrance.Id, Remote, f.Clock.Now));

        var objects = f.Db.Services.GetRequiredService<ISystemObjects>();
        var inSpace = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            f.Db.Store.ReadAccount(1000001),
            [inSpace],
            selected => new FlightSession(f.Db.Store, selected, f.Clock, new(), [source]),
            1,
            new([], [])
        );
        context.Select(inSpace);
        var flight = context.Flight;
        flight.Snapshot();
        Assert.Throws<InvalidDataException>(() => travel.Transfer(f.PilotId, f.ShipId, source.Id, source.Id));
        Assert.Null(travel.Polarization(f.PilotId, Known, source.Id));
        WormholeTravelHandler handler = new(travel, objects, f.Clock);
        var reply = Assert.IsType<PilotCallResult.Success>(handler.Call(context, "wormholeMgr", "WormholeJump", [source.Id]));
        var replyFields = Assert.IsType<object[]>(reply.Reply.ToBlue());
        Assert.Equal("beyonce", context.ResolveReference(replyFields[0]));
        Assert.Equal(f.Clock.Now + FlightSession.JumpDurationTicks * TimeSpan.TicksPerSecond, replyFields[1]);
        Assert.Contains(flight.Updates, update => update.Command is DestinyCommand.WormholeJumpOut);
        Assert.False(flight.CompleteJump());
        f.Clock.Seconds += FlightSession.JumpDurationTicks + 1;
        Assert.True(flight.CompleteJump());
        Assert.Equal(Remote, flight.Pilot.SystemId);
        Assert.Equal(Remote, f.Db.Store.FindPilot(1000001)!.SystemId);
        Assert.False(flight.CompleteJump());
        flight.Snapshot();
        Assert.Contains(flight.Updates, update => update.Command is DestinyCommand.SpecialEffect { Graphic: "effects.WormholeActivity" });

        var reservoir = Assert.Single(f.Db.Services.GetRequiredService<IMiningSites>().Signatures(Remote, f.Clock.Now));
        Assert.InRange(reservoir.DungeonId, 3555, 3559);
        WorldAsteroid cloud;
        using (WorldDbContext world = new(f.Db.Path))
        {
            cloud = world.Set<WorldAsteroid>().First(a => a.SystemId == Remote);
        }
        f.Db.Store.SetPosition(f.ShipId, new SpacePosition(cloud.X, cloud.Y, cloud.Z), Remote);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        mining.Begin(f.PilotId, scoop, cloud.Id, f.Clock.Now, DogmaEffect.MiningClouds);
        Assert.Equal(cloud.TypeId, mining.Collect(f.PilotId, scoop, cloud.Id, new(1, true, new(.5, .5, .5, .9, .5))).TypeId);
        Assert.Throws<InvalidDataException>(() => travel.Prepare(f.PilotId, f.ShipId, exit.Id));

        f.Db.Store.SetPosition(f.ShipId, exit.Position, Remote);
        Assert.Equal(Known, travel.Transfer(f.PilotId, f.ShipId, exit.Id, source.Id).SystemId);
        f.Db.Store.SetPosition(f.ShipId, source.Position, Known);
        WorldInitialization.Open(f.Db.Path);
        Assert.Equal(WormholeRules.PolarizationSeconds, travel.Polarization(f.PilotId, Known, source.Id)!.Seconds);
        Assert.Throws<InvalidDataException>(() => travel.Prepare(f.PilotId, f.ShipId, source.Id));
        f.Clock.Seconds += WormholeRules.PolarizationSeconds;
        Assert.Null(travel.Polarization(f.PilotId, Known, source.Id));
        Assert.Equal(Remote, travel.Prepare(f.PilotId, f.ShipId, source.Id).System.Id);
        Assert.Equal(pilot.ShipId, f.Db.Store.FindPilot(1000001)!.ShipId);
    }

    private static void AddRoute(Fixture f)
    {
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Database.ExecuteSql(
            $"""
            INSERT INTO SolarSystem VALUES ({Known},'Tama',20000020),({Remote},'J125227',20000020);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                (40000001,{Known},52673,8,2000,0,0,0),(40000002,{Remote},52673,8,2000,0,0,0);
            INSERT INTO ItemGroup VALUES (988,'Wormhole',2);
            INSERT INTO ItemType(Id,Name,GroupId,Radius) VALUES ({Portal},'Wormhole R943',988,3000),(30831,'Wormhole K162',988,3000);
            INSERT INTO DogmaAttribute VALUES (1385,'wormholeMaxJumpMass');
            INSERT INTO TypeAttribute VALUES ({Portal},1385,375000000);
            """
        );
        foreach (var (system, wormholeClass) in new[] { (Known, 8), (Remote, SystemClass) })
        {
            string key = system.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string json = JsonSerializer.Serialize(new { wormholeClassID = wormholeClass });
            catalog.Database.ExecuteSql($"INSERT INTO SourceRecord VALUES ('client/systems',{key},{json})");
        }
    }
}
