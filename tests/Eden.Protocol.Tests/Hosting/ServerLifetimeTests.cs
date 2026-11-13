using Eden.Gameplay.Flight;
using Eden.Modding;
using Eden.Server;
using Eden.Server.Modding;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Hosting;

public sealed class ServerLifetimeTests
{
    [Fact]
    public async Task StopFailureStillRunsRemainingCallbacksAndSavesTheWorld()
    {
        using TestWorld world = new(liveWorld: true);
        var mods = world.Services.GetRequiredService<ModRuntime>();
        var resident = world.Services.GetRequiredService<LiveWorld>();
        var pilot = world.Store.FindPilot(1000001)!;
        SpacePosition position = new(1000, 2000, 3000);
        List<string> order = [];
        mods.Register("test.throwing", new StoppingPlugin(() => throw new InvalidOperationException("Test stop failure.")));
        mods.Register(
            "test.saving",
            new StoppingPlugin(() =>
            {
                order.Add("mod");
                world.Store.SetPosition(pilot.ShipId, position, 30000142);
            })
        );
        mods.Start();

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            ServerLifetime.RunAsync(lifetime =>
            {
                lifetime.OnStop("world", resident.Dispose);
                lifetime.OnStop("mods", mods.Stop);
                lifetime.OnStop("maintenance", () => order.Add("maintenance"));
                lifetime.OnStop("listener", () => throw new IOException("Test listener failure."));
                return Task.CompletedTask;
            })
        );

        Assert.Collection(
            failure.InnerExceptions,
            listener => Assert.IsType<IOException>(listener.InnerException),
            mod => Assert.Contains("test.throwing", mod.InnerException!.ToString(), StringComparison.Ordinal)
        );
        Assert.Equal(["maintenance", "mod"], order);
        Assert.Null(LiveWorld.Find(world.Path));
        Assert.Equal(position, WorldInitialization.Open(world.Path).FindPilot(1000001)!.Position);
    }

    private sealed class StoppingPlugin(Action stop) : IServerMod
    {
        public void Register(IModRegistration registration) => registration.On(ModEventKind.Stopping, _ => stop());
    }
}
