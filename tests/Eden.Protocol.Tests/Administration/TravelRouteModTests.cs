using System.Text.Json;
using System.Text.Json.Nodes;
using Eden.Gameplay.Flight;
using Eden.Modding;
using Eden.Server;
using Eden.Server.Modding;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Modding;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed class TravelRouteModTests
{
    private const int Destination = 30004759;
    private static readonly ModTravelRoute s_route = new(
        "jita-delve",
        new(58000001, 30000142, 16, new(1e11, 1e11, 1e11)),
        new(58000002, Destination, 16, new(2e11, 2e11, 2e11))
    );

    [Fact]
    public void TravelPackagesRequireTheNewApiWhileExistingPackagesRemainCompatible()
    {
        var manifest = Package(s_route).Manifest;
        ModPackages.Validate(manifest with { ApiVersion = ModFormat.ApiVersion });
        var travel = manifest with { Files = [new(ModTravelRoutes.PackagePath, new string('A', 64), 1)] };
        ModPackages.Validate(travel);
        Assert.Throws<InvalidDataException>(() => ModPackages.Validate(travel with { ApiVersion = ModFormat.ApiVersion }));
    }

    [Theory]
    [InlineData(RegionalGateStyle.Amarr, 29626, 29625)]
    [InlineData(RegionalGateStyle.Caldari, 29629, 3873)]
    [InlineData(RegionalGateStyle.Gallente, 29632, 3876)]
    [InlineData(RegionalGateStyle.Minmatar, 29635, 29634)]
    [InlineData(RegionalGateStyle.Concord, 93639, 93639)]
    public void RegionAndBorderModelsKeepTheSourceRegionsStyle(RegionalGateStyle style, int regionType, int borderType)
    {
        Assert.Equal(regionType, RegionalGateRules.Type(style, style));
        foreach (var destination in Enum.GetValues<RegionalGateStyle>().Where(value => value != style))
        {
            Assert.Equal(borderType, RegionalGateRules.Type(style, destination));
            Assert.Equal(style, RegionalGateRules.Style(RegionalGateRules.Type(style, destination)));
        }
        Assert.Null(RegionalGateRules.Style(12292));
        Assert.Null(RegionalGateRules.Style(56317));
        Assert.Null(RegionalGateRules.Style(93250));
    }

    [Fact]
    public async Task ComposedGateTravelUsesResidentStateAndSurvivesCheckpointReopen()
    {
        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        Seed(database.CatalogPath);
        var path = Path.Combine(Path.GetDirectoryName(world.Path)!, "express.sqlite");
        var snapshot = new ModStore(database).Prepare().Snapshot;
        string skills = SkillCatalogProjection.Create(database.CatalogPath, path, snapshot);
        var overlay = TravelRouteProjection.Apply(path, skills, [Package(s_route)]);
        var document = JsonNode.Parse(overlay)!;
        Assert.Equal(2, document["travel"]!["jumps"]!.AsArray().Count);
        Assert.Equal(s_route.B.GateId, document["travel"]!["content"]!["30000142"]!["58000001"]!["destination"]!.GetValue<int>());
        using (CatalogDbContext projected = new(path))
        {
            Assert.Equal(
                Destination,
                projected.GatesInSystem(s_route.A.SystemId).Single(gate => gate.Id == s_route.A.GateId).DestinationSystemId
            );
            Assert.Equal(s_route.A.GateId, projected.GatesInSystem(Destination).Single(gate => gate.Id == s_route.B.GateId).DestinationId);
        }
        // Use the same catalog selection boundary as normal restart activation, without a client archive.
        string marker = Path.GetFullPath(world.Path) + ".skills-active";
        string set = new string('A', 64);
        string directory = Path.GetFullPath(world.Path) + ".skill-packages/" + set;
        Directory.CreateDirectory(directory);
        File.Copy(path, Path.Combine(directory, "catalog.sqlite"));
        File.WriteAllText(Path.Combine(directory, "skills.json"), "[]");
        File.WriteAllText(
            Path.Combine(directory, "package.json"),
            JsonSerializer.Serialize(new SkillClientPackage(set, 0, new string('B', 64), 1), ModFormat.Json)
        );
        File.WriteAllText(marker, set);
        WorldDatabase active = new(world.Path);
        SpacePosition arrived;
        using (var services = ServerServices.Build(active, liveWorld: true, simulationObjects: false))
        {
            var store = services.GetRequiredService<SqliteWorld>();
            var pilot = store.FindPilot(1000001)!;
            var gates = services.GetRequiredService<IPilotGateTravel>();
            var objects = services.GetRequiredService<ISystemObjects>();
            var source = Assert.Single(objects.Load(pilot.SystemId), item => item.Id == s_route.A.GateId);
            Assert.Equal(Destination, source.DestinationSystemId);
            Assert.Equal(s_route.B.GateId, source.DestinationGateId);
            Assert.Throws<InvalidDataException>(() => gates.Transfer(pilot.PilotId, pilot.ShipId, source.Id, s_route.B.GateId));
            store.SetPosition(pilot.ShipId, s_route.A.Position.ToSpacePosition() + new SpacePosition(1_000_000, 0, 0));
            Assert.Throws<InvalidDataException>(() => gates.Transfer(pilot.PilotId, pilot.ShipId, source.Id, s_route.B.GateId));
            Assert.Equal(pilot.SystemId, store.FindPilot(1000001)!.SystemId);
            store.SetPosition(pilot.ShipId, s_route.A.Position.ToSpacePosition());
            Assert.Throws<InvalidDataException>(() => gates.Transfer(pilot.PilotId, pilot.ShipId, source.Id, 50002185));
            Assert.Equal(s_route.A.Position.ToSpacePosition(), store.FindPilot(1000001)!.Position);
            var transferred = gates.Transfer(pilot.PilotId, pilot.ShipId, source.Id, s_route.B.GateId);
            Assert.Equal(Destination, transferred.SystemId);
            arrived = transferred.Position!;
            Assert.InRange(arrived.DistanceTo(s_route.B.Position.ToSpacePosition()), 15000, 16000);
            await services.GetRequiredService<LiveWorld>().FlushAsync();
        }
        using var reopened = ServerServices.Build(new(world.Path), liveWorld: true, simulationObjects: false);
        var restored = reopened.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        Assert.Equal(Destination, restored.SystemId);
        Assert.Equal(arrived, restored.Position);
        reopened.GetRequiredService<SqliteWorld>().SetPosition(restored.ShipId, s_route.B.Position.ToSpacePosition());
        var returned = reopened
            .GetRequiredService<IPilotGateTravel>()
            .Transfer(restored.PilotId, restored.ShipId, s_route.B.GateId, s_route.A.GateId);
        Assert.Equal(s_route.A.SystemId, returned.SystemId);
    }

    [Theory]
    [InlineData("same-system")]
    [InlineData("duplicate-id")]
    [InlineData("nonfinite")]
    [InlineData("outside-range")]
    public void InvalidRouteDefinitionsAreRejected(string variant)
    {
        var route = variant switch
        {
            "same-system" => s_route with { B = s_route.B with { SystemId = s_route.A.SystemId } },
            "duplicate-id" => s_route with { B = s_route.B with { GateId = s_route.A.GateId } },
            "nonfinite" => s_route with { A = s_route.A with { Position = new(double.NaN, 0, 0) } },
            _ => s_route with { A = s_route.A with { GateId = 1 } },
        };
        Assert.Throws<InvalidDataException>(() => ModTravelRoutes.Validate([route]));
    }

    [Theory]
    [InlineData("duplicate-link")]
    [InlineData("occupied-id")]
    [InlineData("wrong-type")]
    [InlineData("overlap")]
    [InlineData("unknown-system")]
    public void InvalidCompositionRollsBackCatalog(string variant)
    {
        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        Seed(database.CatalogPath);
        var second = variant switch
        {
            "duplicate-link" => s_route,
            "occupied-id" => s_route with { B = s_route.B with { GateId = s_route.A.GateId } },
            "wrong-type" => s_route with { A = s_route.A with { TypeId = 601 } },
            "overlap" => s_route with { A = s_route.A with { Position = new(0, 0, 0) } },
            _ => s_route with { B = s_route.B with { SystemId = 39999999 } },
        };
        var packages = variant == "duplicate-link" ? new[] { Package(s_route), Package(second) } : new[] { Package(second) };
        Assert.Throws<InvalidDataException>(() => TravelRouteProjection.Apply(database.CatalogPath, "{}", packages));
        using CatalogDbContext catalog = new(database.CatalogPath);
        Assert.DoesNotContain(catalog.Definitions.Gates.Values, gate => gate.Id >= ModTravelRoutes.FirstGateId);
        Assert.DoesNotContain(
            catalog.CelestialsInSystem(s_route.A.SystemId),
            body => body.Id >= ModTravelRoutes.FirstGateId && body.Id < ModTravelRoutes.GateEndId
        );
    }

    [PinnedSkillClientFact]
    public async Task RoutePackagePublishesActivatesAndDisablesWithoutChangingOriginalCatalog()
    {
        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        Seed(database.CatalogPath);
        var original = File.ReadAllBytes(database.CatalogPath);
        var store = new ModStore(database);
        string archive = Path.Combine(Path.GetDirectoryName(world.Path)!, "express.edenmod");
        var package = Package(s_route);
        ModPackages.Write(archive, package.Manifest with { CatalogFingerprint = store.Reference().CatalogFingerprint }, package.Contents);
        var hash = store.Install(archive);
        store.Enable(hash, true);
        var first = await new ModPublisher(database, store).Build(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!);
        Assert.True(store.BeginActivation());
        store.CompleteActivation();
        Assert.Contains(new WorldDatabase(world.Path).Catalog.Gates.Values, gate => gate.Id == s_route.A.GateId);
        Assert.Equal(original, File.ReadAllBytes(database.OriginalCatalogPath));
        store.Enable(hash, false);
        var disabled = await new ModPublisher(new(world.Path), store).Build(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!);
        Assert.NotEqual(first.Id, disabled.Id);
        Assert.True(store.BeginActivation());
        store.CompleteActivation();
        Assert.DoesNotContain(new WorldDatabase(world.Path).Catalog.Gates.Values, gate => gate.Id == s_route.A.GateId);
        Assert.Equal(original, File.ReadAllBytes(database.OriginalCatalogPath));
    }

    private static ModPackage Package(ModTravelRoute route) =>
        new(
            new ModManifest
            {
                Id = "test.express",
                Name = "Express",
                ApiVersion = ModFormat.TravelApiVersion,
                CatalogFingerprint = new string('A', 64),
            },
            "test",
            new Dictionary<string, byte[]>
            {
                [ModTravelRoutes.PackagePath] = JsonSerializer.SerializeToUtf8Bytes(new[] { route }, ModFormat.Json),
            }
        );

    private static void Seed(string path)
    {
        using CatalogDbContext catalog = new(path, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO Region(Id,Name) VALUES (10000060,'Delve');
            INSERT INTO Constellation(Id,RegionId,Name) VALUES (20009999,10000060,'Express test');
            INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES (30004759,20009999,'1DQ1-A');
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                (50009991,30004759,16,10,1e9,2e9,3e9),(50009992,30000144,16,10,3e9,2e9,1e9);
            INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES (50009991,30004759,50009992),(50009992,30000144,50009991);
            """
        );
        foreach (int system in new[] { 30000142, Destination })
        {
            string key = system.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string data = "{\"center\":{\"x\":1,\"y\":2,\"z\":3},\"neighbours\":[]}";
            catalog.Database.ExecuteSql($"INSERT INTO SourceRecord VALUES ('client/systems',{key},{data})");
            data = "{\"stargates\":{\"50009991\":{\"typeID\":16,\"destination\":50009992,\"position\":{\"x\":0,\"y\":0,\"z\":0}}}}";
            catalog.Database.ExecuteSql($"INSERT INTO SourceRecord VALUES ('client/solarsystemcontent',{key},{data})");
        }
    }
}
