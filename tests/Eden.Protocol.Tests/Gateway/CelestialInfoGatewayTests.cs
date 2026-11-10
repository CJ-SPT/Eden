using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Content;
using Eden.Gameplay.Maps;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Gateway;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Storage;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Planets = Eden.Protocol.Gateway.CelestialResources.Planets;
using Stars = Eden.Protocol.Gateway.CelestialResources.Stars;

namespace Eden.Protocol.Tests.Gateway;

public sealed class CelestialInfoGatewayTests
{
    private const string PlanetApi = "eve_public.sovereignty.resource.planet.api.";
    private const string StarApi = "eve_public.sovereignty.resource.star.api.";
    private const long PowerPlanet = 40000001;
    private const long WorkforcePlanet = 40000002;
    private const long ReagentPlanet = 40000003;

    [Fact]
    public void SunInfoCanPrimeTheStarResourceConfigurationInsteadOfFailingTheWindow()
    {
        var request = new Request
        {
            Payload = new Any { TypeUrl = "type.googleapis.com/eve_public.sovereignty.resource.star.api.GetAllConfigurationsRequest" },
        };
        var response = GatewayRequestsTests.Create().Handle(request);
        Assert.Equal(200u, response.StatusCode);
        Assert.Equal("type.googleapis.com/eve_public.sovereignty.resource.star.api.GetAllConfigurationsResponse", response.Payload.TypeUrl);
        Assert.Empty(response.Payload.Value);
        Assert.Empty(response.Payload.Unpack<Stars.GetAllConfigurationsResponse>().Configurations);
    }

    [Fact]
    public void PlanetInfoReadsCatalogPowerWorkforceAndReagentAmountsThroughTheRealGateway()
    {
        using TestWorld db = new();
        Seed(db, PowerPlanet, "{\"power\":740}");
        Seed(db, WorkforcePlanet, "{\"workforce\":3490}");
        Seed(
            db,
            ReagentPlanet,
            """
            {"reagent":{"type_id":81143,"amount_per_cycle":2760,"cycle_period":3600,
              "secured_capacity":4320,"unsecured_capacity":8640}}
            """
        );
        var gateway = db.Services.GetRequiredService<GatewayRequests>();
        var request = Request(PlanetApi + "GetAllDefinitionsRequest", new Planets.GetAllDefinitionsRequest { NoKnownVersion = true });
        var response = gateway.Handle(request);
        Assert.Equal(200u, response.StatusCode);
        Assert.Equal(request.ApplicationInstanceUuid, response.ApplicationInstanceUuid);
        Assert.Equal(request.CorrelationUuid, response.CorrelationUuid);
        var definitions = response.Payload.Unpack<Planets.GetAllDefinitionsResponse>();
        Assert.Equal(new ulong[] { PowerPlanet, WorkforcePlanet, ReagentPlanet }, definitions.Definitions.Select(p => p.Planet.Sequential));
        Assert.Equal(740ul, definitions.Definitions[0].Power);
        Assert.Equal(0ul, definitions.Definitions[0].Workforce);
        Assert.Empty(definitions.Definitions[0].ReagentDefinitions);
        Assert.Equal(3490ul, definitions.Definitions[1].Workforce);
        Assert.Equal(0ul, definitions.Definitions[1].Power);
        var reagent = Assert.Single(definitions.Definitions[2].ReagentDefinitions);
        Assert.Equal(81143ul, reagent.ReagentType.Sequential);
        Assert.Equal(reagent.ReagentType, reagent.Type);
        Assert.Equal(2760u, reagent.Definition.AmountPerCycle);
        Assert.Equal(3600, reagent.Definition.CyclePeriod.Seconds);
        Assert.Equal(0, reagent.Definition.CyclePeriod.Nanos);
        Assert.Equal(4320u, reagent.Definition.SecuredCapacity);
        Assert.Equal(8640u, reagent.Definition.UnsecuredCapacity);
        // Independently serialized with the descriptors extracted as inert data from
        // the original 3662168 planet/planet_pb2.pyj and its identifier/reagent imports.
        Assert.Equal(
            Convert.FromHexString("0A050883B48913221C0A0408F7F904120E08C815120308901C20E02128C0431A0408F7F904"),
            definitions.Definitions[2].ToByteArray()
        );
        Assert.Equal((uint)BuildContentPolicy.PinnedBuild, definitions.Version.Major);
        Assert.Equal(64, Assert.Single(definitions.Version.BuildTags).Length);
        var cached = gateway.Handle(
            Request(PlanetApi + "GetAllDefinitionsRequest", new Planets.GetAllDefinitionsRequest { KnownVersion = definitions.Version })
        );
        Assert.Equal(304u, cached.StatusCode);
        Assert.Null(cached.Payload);
        var exactVersion = gateway.Handle(
            Request(PlanetApi + "GetDefinitionsVersionRequest", new Planets.GetDefinitionsVersionRequest { Version = definitions.Version })
        );
        Assert.Equal(200u, exactVersion.StatusCode);
        Assert.Equal(definitions.Definitions, exactVersion.Payload.Unpack<Planets.GetDefinitionsVersionResponse>().Definitions);
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Equal(response.Payload, reopened.GetRequiredService<GatewayRequests>().Handle(request).Payload);
        var directory = db.Services.GetRequiredService<ICelestialResourceDirectory>();
        var snapshot = directory.Read();
        using var metrics = SessionPerformance.Begin("warm-celestial-resources", _ => { }, force: true)!;
        Assert.Same(snapshot, directory.Read());
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }

    [Fact]
    public void ResourceVersionChangesWithCatalogValuesAndOldVersionsAreNotInvented()
    {
        using TestWorld db = new();
        Seed(db, PowerPlanet, "{\"power\":740}");
        var gateway = db.Services.GetRequiredService<GatewayRequests>();
        var first = gateway.Handle(Request(PlanetApi + "GetAllDefinitionsRequest", new Planets.GetAllDefinitionsRequest()));
        var before = first.Payload.Unpack<Planets.GetAllDefinitionsResponse>();
        var snapshot = db.Services.GetRequiredService<ICelestialResourceDirectory>().Read();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            var row = catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == "sde/planetResources");
            row.Data = "{\"power\":741}";
            catalog.SaveChanges();
        }
        var updated = gateway.Handle(
            Request(PlanetApi + "GetAllDefinitionsRequest", new Planets.GetAllDefinitionsRequest { KnownVersion = before.Version })
        );
        Assert.Equal(200u, updated.StatusCode);
        var after = updated.Payload.Unpack<Planets.GetAllDefinitionsResponse>();
        Assert.NotEqual(before.Version, after.Version);
        Assert.Equal(741ul, Assert.Single(after.Definitions).Power);
        Assert.Equal(740, Assert.Single(snapshot.Planets).Power);
        var stale = gateway.Handle(
            Request(PlanetApi + "GetDefinitionsVersionRequest", new Planets.GetDefinitionsVersionRequest { Version = before.Version })
        );
        Assert.Equal(404u, stale.StatusCode);
        Assert.Null(stale.Payload);
    }

    [Fact]
    public void EmptyResourceCatalogStillPrimesAVersionAndAbsentVersionReadsReturnNotFound()
    {
        var gateway = GatewayRequestsTests.Create();
        var response = gateway.Handle(Request(PlanetApi + "GetAllDefinitionsRequest", new Planets.GetAllDefinitionsRequest()));
        Assert.Equal(200u, response.StatusCode);
        var definitions = response.Payload.Unpack<Planets.GetAllDefinitionsResponse>();
        Assert.NotNull(definitions.Version);
        Assert.Empty(definitions.Definitions);
        var absent = gateway.Handle(Request(PlanetApi + "GetDefinitionsVersionRequest", new Planets.GetDefinitionsVersionRequest()));
        Assert.Equal(404u, absent.StatusCode);
        Assert.Null(absent.Payload);
    }

    [Theory]
    [InlineData(PlanetApi + "GetAllDefinitionsRequest")]
    [InlineData(PlanetApi + "GetDefinitionsVersionRequest")]
    [InlineData(StarApi + "GetAllConfigurationsRequest")]
    public void MalformedResourcePayloadsReturnBadRequestWithoutBreakingOtherGatewayRoutes(string type)
    {
        var request = Request(type, new Planets.GetAllDefinitionsRequest());
        request.Payload.Value = ByteString.CopyFrom(0x0a, 0xff);
        var response = GatewayRequestsTests.Create().Handle(request);
        Assert.Equal(400u, response.StatusCode);
        Assert.Equal(request.CorrelationUuid, response.CorrelationUuid);
        Assert.Null(response.Payload);
    }

    [Theory]
    [InlineData("{\"power\":-1}")]
    [InlineData("{\"workforce\":-1}")]
    [InlineData("{\"reagent\":{\"type_id\":81143,\"cycle_period\":0}}")]
    [InlineData("{\"reagent\":{\"type_id\":81143,\"cycle_period\":3600,\"amount_per_cycle\":4294967296}}")]
    [InlineData("{\"reagent\":{\"type_id\":81143,\"cycle_period\":3600,\"secured_capacity\":-1}}")]
    public void InvalidCatalogResourceValuesCannotWrapIntoUnsignedWireValues(string data)
    {
        using TestWorld db = new();
        Seed(db, PowerPlanet, data);
        Assert.Throws<InvalidDataException>(() => db.Services.GetRequiredService<ICelestialResourceDirectory>().Read());
    }

    private static Request Request(string type, IMessage payload) =>
        new()
        {
            ApplicationInstanceUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            CorrelationUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Payload = new Any { TypeUrl = "type.googleapis.com/" + type, Value = payload.ToByteString() },
        };

    private static void Seed(TestWorld world, long id, string data)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath, writable: true);
        catalog.Add(
            new SourceRecord
            {
                Dataset = "sde/planetResources",
                Key = id.ToString(CultureInfo.InvariantCulture),
                Data = data,
            }
        );
        catalog.SaveChanges();
    }
}
