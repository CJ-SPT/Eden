using Eden.Gameplay.Sovereignty;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Gateway;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Sovereignty;
using Eden.Server.Authentication;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Fuel = Eden.Protocol.Gateway.Sovereignty.FuelApi;
using HubId = Eden.Protocol.Gateway.Sovereignty.Hub.Identifier;
using Identifier = Eden.Protocol.Gateway.Sovereignty.Identifier;
using Upgrade = Eden.Protocol.Gateway.Sovereignty.UpgradeApi;
using Workforce = Eden.Protocol.Gateway.Sovereignty.WorkforceApi;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyResourceProtocolTests
{
    [Fact]
    public void PinnedGatewayFieldNumbersPreserveNativeFuelAndWorkforceWireShapes()
    {
        var fuel = new Fuel.AddRequest
        {
            FuelItem = new() { Sequential = 17 },
            Amount = 5,
            Hub = new() { Sequential = 23 },
        };
        Assert.Equal("0A02081110051A020817", Convert.ToHexString(fuel.ToByteArray()));
        var workforce = new Workforce.ConfigureRequest
        {
            Hub = new() { Sequential = 23 },
            Configuration = new() { Transit = true },
        };
        Assert.Equal("0A02081712022001", Convert.ToHexString(workforce.ToByteArray()));
    }

    [Fact]
    public void GatewayRequiresAuthenticatedAccountOwnershipAndPublishesCommittedInventoryAndStateNotices()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        var hook = SovereigntyUpgradeTests.Deploy(fixture, workforce: true);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
        SovereigntyUpgradeTests.MoveToHub(fixture, hub);
        var gateway = fixture.World.Services.GetRequiredService<GatewayRequests>();
        var definitions = gateway.Handle(Request(new Upgrade.GetDefinitionsRequest()));
        Assert.Equal(200u, definitions.StatusCode);
        Assert.Equal(44, definitions.Payload.Unpack<Upgrade.GetDefinitionsResponse>().Definitions.Count);
        long item = fixture.Grant(82499);
        var configure = new Upgrade.ProcessConfigurationRequest { Hub = new() { Sequential = (ulong)hub } };
        configure.NewUpgrades.Add(new Identifier { Sequential = (ulong)item });
        configure.Configuration.Add(
            new Upgrade.ProcessConfigurationRequest.Types.UpgradeConfiguration
            {
                UpgradeType = new() { Sequential = 82499 },
                Online = true,
            }
        );
        var request = Request(configure);
        Assert.Equal(401u, gateway.Handle(request).StatusCode);
        Assert.Equal(401u, gateway.Handle(request, 1000002).StatusCode);
        Assert.Equal(1, fixture.Quantity(item));
        var response = gateway.Handle(request, 1000001);
        Assert.Equal(200u, response.StatusCode);
        var installed = Assert.Single(response.Payload.Unpack<Upgrade.ProcessConfigurationResponse>().HubUpgrades.Upgrades);
        Assert.Equal(82499UL, installed.Identifier.UpgradeType.Sequential);
        Assert.Equal(2, installed.Attributes.PowerState);
        Assert.Equal(0, fixture.Quantity(item));
        Assert.Equal(400u, gateway.Handle(request, 1000001).StatusCode);
        Assert.Single(
            fixture
                .World.Services.GetRequiredService<ISovereigntyResources>()
                .Read(SovereigntyCaptureFixture.PilotId, hub)
                .Allocation.Upgrades
        );
        using var scope = fixture.World.Services.CreateScope();
        var pilot = fixture.World.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(selected),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        fixture.World.Services.GetRequiredService<SovereigntyGatewayUpdates>().Advance(context);
        Assert.Contains(context.Notifications, notice => notice is ItemChanged);
        fixture
            .World.Services.GetRequiredService<ISovereigntyResources>()
            .SetSkyhookActive(SovereigntyCaptureFixture.PilotId, hook.Id, false);
        var notices = gateway.SovereigntyNotices(1000001);
        var simulated = Assert.Single(notices, notice => notice.Payload.Is(Upgrade.HubUpgradesSimulatedNotice.Descriptor));
        Assert.Equal(
            3,
            Assert.Single(simulated.Payload.Unpack<Upgrade.HubUpgradesSimulatedNotice>().HubUpgrades.Upgrades).Attributes.PowerState
        );
        Assert.Empty(gateway.SovereigntyNotices(1000001));
        Assert.Empty(gateway.SovereigntyNotices(1000002));
    }

    [Fact]
    public void ProofBoundGatewayCredentialExpiresWithItsGameConnectionLease()
    {
        using SovereigntyCaptureFixture fixture = new();
        var identity = fixture.World.Services.GetRequiredService<DevelopmentIdentity>();
        const string ticket = "unit-test-session-ticket";
        Assert.Null(identity.Account("Bearer " + ticket));
        using (identity.OpenGatewayAccess(ticket, 1000001))
        {
            Assert.Equal(1000001, identity.Account("Bearer " + ticket));
            Assert.Null(identity.Account("Bearer different-ticket"));
        }
        Assert.Null(identity.Account("Bearer " + ticket));
    }

    [Fact]
    public void ClosingAnOlderLeaseCannotRevokeItsReplacement()
    {
        using SovereigntyCaptureFixture fixture = new();
        var identity = fixture.World.Services.GetRequiredService<DevelopmentIdentity>();
        using var original = identity.OpenGatewayAccess("reused-development-ticket", 1000001);
        using var replacement = identity.OpenGatewayAccess("reused-development-ticket", 1000001);
        original.Dispose();
        Assert.Equal(1000001, identity.Account("Bearer reused-development-ticket"));
        replacement.Dispose();
        Assert.Null(identity.Account("Bearer reused-development-ticket"));
    }

    [Fact]
    public void AuthenticatedNativeQueriesRejectForeignPilotMalformedPayloadAndForeignHubWithoutWrites()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        var gateway = fixture.World.Services.GetRequiredService<GatewayRequests>();
        var get = new Upgrade.GetHubUpgradesRequest { Hub = new HubId { Sequential = (ulong)hub } };
        Assert.Equal(401u, gateway.Handle(Request(get, SovereigntyCaptureFixture.OtherPilotId), 1000001).StatusCode);
        Assert.Equal(403u, gateway.Handle(Request(get, SovereigntyCaptureFixture.OtherPilotId), 1000002).StatusCode);
        var malformed = Request(get);
        malformed.Payload.Value = ByteString.CopyFrom([0x80]);
        Assert.Equal(400u, gateway.Handle(malformed, 1000001).StatusCode);
        Assert.Empty(
            fixture
                .World.Services.GetRequiredService<ISovereigntyResources>()
                .Read(SovereigntyCaptureFixture.PilotId, hub)
                .Allocation.Upgrades
        );
    }

    [Fact]
    public void NativeCorporationLaunchAndComponentAuthorizationRejectDuplicateDeploymentWithoutDebiting()
    {
        using SovereigntyCaptureFixture fixture = new();
        var existing = SovereigntyUpgradeTests.Deploy(fixture, workforce: true);
        // The first deployment leaves the ship beside its planet; choose the other workforce planet.
        var directory = fixture.World.Services.GetRequiredService<Eden.Gameplay.Maps.ICelestialResourceDirectory>();
        using var catalog = new Eden.World.Catalog.CatalogDbContext(new Eden.World.Storage.WorldDatabase(fixture.World.Path).CatalogPath);
        var bodies = catalog.CelestialsInSystem(SovereigntyCaptureFixture.SystemId);
        var planet = directory
            .Read()
            .Planets.First(row => row.Workforce > 1800 && row.PlanetId != existing.PlanetId && bodies.Any(body => body.Id == row.PlanetId));
        var body = bodies.Single(row => row.Id == planet.PlanetId);
        fixture.Position(new(body.X + (body.Radius ?? 0) + 5_000_000, body.Y, body.Z));
        long stack = fixture.Grant(SovereigntyResourceRules.SkyhookTypeId, 2);
        using (var action = fixture.Live.Economy.Begin(Eden.World.Storage.TransactionParticipants.None))
        {
            action.Set<Eden.Data.World.Inventory.OwnedItem>().Find(stack)!.OwnerId = fixture.CorporationId;
            action.Commit();
        }
        using var scope = fixture.World.Services.CreateScope();
        var pilot = fixture.World.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(selected),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object?[] launch = [new List<object?> { new object?[] { stack, 1L } }, (long)fixture.CorporationId, false];
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "ship", "LaunchDrones", launch, _ => { }));
        Assert.Equal(1, fixture.Quantity(stack));
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "ship", "LaunchDrones", launch, _ => { }));
        Assert.Equal(1, fixture.Quantity(stack));
        var hooks = fixture.World.Services.GetRequiredService<ISovereigntyResources>().Skyhooks(SovereigntyCaptureFixture.PilotId);
        Assert.Equal(2, hooks.Count);
        Assert.Throws<InvalidDataException>(() =>
            fixture
                .World.Services.GetRequiredService<Eden.Gameplay.Structures.IPilotStructures>()
                .Unanchor(SovereigntyCaptureFixture.PilotId, hooks[1].Id)
        );
        var handler = scope.ServiceProvider.GetRequiredService<SovereigntyResourceHandler>();
        var authorized = Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "beyonce", "CallComponentFromClient", [hooks[1].Id, "orbitalSkyhook", "IsCharacterAuthorised"])
        );
        Assert.Equal(
            true,
            Eden.Protocol.Serialization.BlueMarshal.Decode(Eden.Protocol.Serialization.BlueMarshal.Encode(authorized.Reply))
        );
    }

    [Fact]
    public async Task Http2GatewayForwardsBearerCredentialAndRejectsUnauthenticatedContext()
    {
        using SovereigntyCaptureFixture fixture = new();
        long hub = SovereigntyUpgradeTests.Claim(fixture);
        var identity = fixture.World.Services.GetRequiredService<DevelopmentIdentity>();
        using var lease = identity.OpenGatewayAccess("transport-ticket", 1000001);
        await using var app = Eden.Server.Gateway.LocalGateway.Create(
            fixture.World.Services.GetRequiredService<GatewayRequests>(),
            new() { GatewayPort = 0 },
            identity
        );
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false });
        var message = Request(new Upgrade.GetHubUpgradesRequest { Hub = new() { Sequential = (ulong)hub } });
        byte[] frame = new byte[5 + message.CalculateSize()];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), frame.Length - 5);
        message.ToByteArray().CopyTo(frame, 5);
        foreach (bool authenticated in new[] { false, true })
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                new Uri(new Uri(Assert.Single(app.Urls)), "/eve_public.gateway.Requests/Send")
            )
            {
                Version = System.Net.HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(frame),
            };
            request.Content.Headers.ContentType = new("application/grpc");
            request.Headers.TE.ParseAdd("trailers");
            if (authenticated)
            {
                request.Headers.Authorization = new("Bearer", "transport-ticket");
            }
            using var reply = await client.SendAsync(request, timeout.Token);
            byte[] response = await reply.Content.ReadAsByteArrayAsync(timeout.Token);
            var parsed = Response.Parser.ParseFrom(response.AsSpan(5));
            Assert.Equal(authenticated ? 200u : 401u, parsed.StatusCode);
        }
        await app.StopAsync(timeout.Token);
    }

    [Fact]
    public void ObservedSkyhooksPublishActivationAndCommittedStockChangesOnce()
    {
        using SovereigntyCaptureFixture fixture = new();
        var hook = SovereigntyUpgradeTests.Deploy(fixture, workforce: false);
        var gateway = fixture.World.Services.GetRequiredService<GatewayRequests>();
        var get = Request(new Eden.Protocol.Gateway.Sovereignty.SkyhookApi.GetRequest { Skyhook = new() { Sequential = (ulong)hook.Id } });
        Assert.Equal(200u, gateway.Handle(get, 1000001).StatusCode);
        Assert.Empty(gateway.SovereigntyNotices(1000001));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(90));
        var notices = gateway.SovereigntyNotices(1000001);
        Assert.Single(notices, notice => notice.Payload.Is(Eden.Protocol.Gateway.Sovereignty.SkyhookApi.ActivationNotice.Descriptor));
        var stock = Assert.Single(
            notices,
            notice => notice.Payload.Is(Eden.Protocol.Gateway.Sovereignty.SkyhookApi.ReagentSimulationsNotice.Descriptor)
        );
        Assert.Equal(
            321u,
            Assert
                .Single(stock.Payload.Unpack<Eden.Protocol.Gateway.Sovereignty.SkyhookApi.ReagentSimulationsNotice>().Simulations)
                .Simulation.SecuredStock
        );
        Assert.Empty(gateway.SovereigntyNotices(1000001));
        var structure = fixture.Live.Economy.Inspect(world => world.Set<Eden.Data.World.Structures.SavedStructure>().Find(hook.Id)!);
        fixture.Position(new(structure.X + 1000, structure.Y, structure.Z));
        fixture.World.Services.GetRequiredService<ISovereigntyResources>().CollectReagents(SovereigntyCaptureFixture.PilotId, hook.Id, 1);
        stock = Assert.Single(gateway.SovereigntyNotices(1000001));
        Assert.Equal(
            320u,
            Assert
                .Single(stock.Payload.Unpack<Eden.Protocol.Gateway.Sovereignty.SkyhookApi.ReagentSimulationsNotice>().Simulations)
                .Simulation.SecuredStock
        );
        Assert.Empty(gateway.SovereigntyNotices(1000002));
        Assert.Equal(
            200u,
            gateway
                .Handle(
                    Request(
                        new Eden.Protocol.Gateway.Sovereignty.SkyhookApi.DeactivateRequest
                        {
                            Skyhook = new() { Sequential = (ulong)hook.Id },
                        }
                    ),
                    1000001
                )
                .StatusCode
        );
        notices = gateway.SovereigntyNotices(1000001);
        var activation = Assert.Single(
            notices,
            notice => notice.Payload.Is(Eden.Protocol.Gateway.Sovereignty.SkyhookApi.ActivationNotice.Descriptor)
        );
        Assert.False(activation.Payload.Unpack<Eden.Protocol.Gateway.Sovereignty.SkyhookApi.ActivationNotice>().Active);
        Assert.Empty(gateway.SovereigntyNotices(1000001));
    }

    internal static Request Request(IMessage payload, long pilot = SovereigntyCaptureFixture.PilotId) =>
        new()
        {
            Payload = Any.Pack(payload),
            CorrelationUuid = ByteString.CopyFrom([1, 2, 3]),
            AuthoritativeContext = new() { Identity = new() { Character = new() { Sequential = checked((uint)pilot) } } },
        };
}
