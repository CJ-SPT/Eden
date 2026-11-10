using System.Text.Json;
using Eden.Modding;
using Eden.Protocol.Flight;
using Eden.Protocol.Modding;
using Eden.Protocol.PilotSessions;
using Eden.Server;
using Eden.Server.Modding;
using Eden.World.Modding;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed class ModRuntimeTests
{
    [PinnedSkillClientFact]
    public async Task TrustedPluginCommandsEnforceRolesPersistStateAndIsolateFailures()
    {
        using TestWorld world = Eden.Protocol.Tests.Persistence.RedemptionTests.Create();
        var database = new WorldDatabase(world.Path);
        var store = new ModStore(database);
        string archive = Path.Combine(Path.GetDirectoryName(world.Path)!, "fixture.edenmod");
        var manifest = new ModManifest
        {
            Id = "test.runtime",
            Name = "Runtime fixture",
            CatalogFingerprint = store.Reference().CatalogFingerprint,
            ClientEntry = "client/main.py",
            Server = new("server/Fixture.dll", typeof(ModFixturePlugin).FullName!),
        };
        ModPackages.Write(
            archive,
            manifest,
            new Dictionary<string, byte[]>
            {
                ["server/Fixture.dll"] = File.ReadAllBytes(typeof(ModFixturePlugin).Assembly.Location),
                ["client/main.py"] = System.Text.Encoding.UTF8.GetBytes("def register(api):\n    api.menu('Fixture', lambda: None)\n"),
            }
        );
        string hash = store.Install(archive);
        store.Enable(hash, true);
        var built = await new ModPublisher(database, store).Build(
            Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!,
            CancellationToken.None
        );
        using (var archiveContents = System.IO.Compression.ZipFile.OpenRead(Path.Combine(store.SetDirectory(built.Id), "code.ccp")))
        {
            Assert.NotNull(archiveContents.GetEntry("eden_mod_loader.pyj"));
            Assert.Contains(
                archiveContents.Entries,
                value =>
                    value.FullName.StartsWith("eden_mod_", StringComparison.Ordinal)
                    && value.FullName.EndsWith("/main.pyj", StringComparison.Ordinal)
            );
        }
        Assert.True(store.BeginActivation());
        using var services = ServerServices.Build(new WorldDatabase(world.Path));
        var runtime = services.GetRequiredService<ModRuntime>();
        runtime.Start();
        store.CompleteActivation();
        Assert.Equal("true", store.ReadState("test.runtime", "started"));
        Assert.Null(store.ReadState("other.mod", "started"));
        var pilot = new ModPilot(1000001, 90000001, 0, 60003760, 30000142);
        var reply = runtime.Execute("test.runtime", "hello", new(pilot, "hello"));
        Assert.True(reply.Success);
        Assert.Equal("\"hello\"", reply.Json);
        Assert.False(runtime.Execute("test.runtime", "admin", new(pilot, "")).Success);
        Assert.True(runtime.Execute("test.runtime", "admin", new(pilot with { Roles = long.MaxValue }, "")).Success);
        Assert.False(runtime.Execute("test.runtime", "hello", new(pilot, "fail")).Success);
        Assert.False(runtime.Execute("test.runtime", "hello", new(pilot, "bad-json")).Success);
        Assert.False(runtime.Execute("test.runtime", "missing", new(pilot, "")).Success);
        var reward = runtime.Execute("test.runtime", "reward", new(pilot, ""));
        Assert.True(reward.Success);
        Assert.Equal(reward.Json, runtime.Execute("test.runtime", "reward", new(pilot, "")).Json);
        runtime.Publish(new(ModEventKind.PilotSelected, pilot));
        Assert.Equal("90000001", new ModStore(new WorldDatabase(world.Path)).ReadState("test.runtime", "selected"));
        runtime.Publish(new(ModEventKind.LocationChanged, pilot));
        Assert.Equal("1", store.ReadState("test.runtime", "locations"));
        runtime.Publish(new(ModEventKind.PilotDisconnected, pilot));
        Assert.Equal("true", store.ReadState("test.runtime", "disconnected"));
        Assert.Throws<InvalidOperationException>(() => runtime.Publish(new(ModEventKind.LocationChanged, pilot)));
        Assert.True(runtime.FailureToken.IsCancellationRequested);
        Assert.Equal(built.Id, store.Status().Active!.Id);
    }

    [Fact]
    public void WireAdapterRejectsMalformedArgumentsAndPreservesStructuredReplies()
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        ModCommandHandler handler = new(new FakeCommands());
        Assert.IsType<PilotCallResult.UnsupportedCall>(handler.Call(context, "wrong", "Execute", []));
        var failed = Assert.IsType<PilotCallResult.Success>(handler.Call(context, "edenMods", "Execute", ["test.mod"]));
        var wire = Assert.IsType<Dictionary<string, object?>>(failed.Reply.ToBlue());
        Assert.Equal(false, wire["success"]);
    }

    [Fact]
    public void SessionObservationPublishesOnlyMembershipChangesAndDisconnectsOnce()
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        var events = new CapturedEvents();
        var observer = new ModSessionObserver(context, events);
        observer.Observe();
        Assert.Empty(events.Events);
        context.Select(pilot);
        observer.Observe();
        observer.Observe();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            observer.Observe();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.Equal(ModEventKind.PilotSelected, Assert.Single(events.Events).Kind);
        context.LogOff();
        observer.Observe();
        observer.Dispose();
        Assert.Equal([ModEventKind.PilotSelected, ModEventKind.PilotDisconnected], events.Events.Select(value => value.Kind));
    }

    private sealed class CapturedEvents : IModSessionEvents
    {
        public List<ModEvent> Events { get; } = [];

        public void Publish(ModEvent notification) => Events.Add(notification);
    }

    private sealed class FakeCommands : IModCommands
    {
        public ModCommandReply Execute(string modId, string command, ModCommandContext context) => ModCommandReply.Ok("{}");
    }
}

public sealed class ModFixturePlugin : IServerMod
{
    public void Register(IModRegistration registration)
    {
        var services = registration.Services;
        registration.On(ModEventKind.Started, _ => services.WriteState("started", "true"));
        registration.On(
            ModEventKind.PilotSelected,
            notification =>
                services.WriteState("selected", notification.Pilot!.PilotId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        );
        registration.On(ModEventKind.PilotDisconnected, _ => services.WriteState("disconnected", "true"));
        registration.On(
            ModEventKind.LocationChanged,
            _ =>
            {
                if (services.ReadState("locations") is not null)
                {
                    throw new InvalidOperationException("Fixture event failure.");
                }
                services.WriteState("locations", "1");
            }
        );
        registration.Command(
            "hello",
            0,
            context =>
                context.Arguments switch
                {
                    "fail" => throw new InvalidOperationException("Fixture command failure."),
                    "bad-json" => ModCommandReply.Ok("invalid"),
                    _ => ModCommandReply.Ok(JsonSerializer.Serialize(context.Arguments)),
                }
        );
        registration.Command(
            "reward",
            0,
            context =>
                ModCommandReply.Ok(JsonSerializer.Serialize(services.Grant(new(context.Pilot.AccountId, "sample", 34, 1, "Mod reward")).Id))
        );
        registration.Command("admin", (long)PilotRole.GameMasterHigh, _ => ModCommandReply.Ok());
    }
}
