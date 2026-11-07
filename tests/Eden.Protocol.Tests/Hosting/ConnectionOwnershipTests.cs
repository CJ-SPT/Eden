using System.Net;
using System.Net.Sockets;
using Eden.Protocol.Transport;
using Eden.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Hosting;

public sealed class ConnectionOwnershipTests
{
    [Fact]
    public async Task EightUnfinishedLoginsDoNotBlockTheNinthGreetingAndOverflowIsClosed()
    {
        using TestWorld world = new();
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var server = world.Services.GetRequiredService<LoopbackServer>();
        var listening = server.ListenAsync(listener, stop.Token);
        List<TcpClient> clients = [];
        try
        {
            for (int index = 0; index < 16; index++)
            {
                TcpClient client = new();
                clients.Add(client);
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
                Assert.NotEmpty(await PacketStream.ReadAsync(client.GetStream(), stop.Token));
            }
            using TcpClient overflow = new();
            await overflow.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
            Assert.Equal(0, await overflow.GetStream().ReadAsync(new byte[1], stop.Token));
        }
        finally
        {
            await stop.CancelAsync();
            foreach (var client in clients)
            {
                client.Dispose();
            }
            await listening;
        }
    }

    private sealed class Cleanup(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public async Task AccountRemainsOccupiedThroughoutScopeCleanup()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using var services = new ServiceCollection()
            .AddScoped(_ => new Cleanup(() =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }))
            .BuildServiceProvider();
        PilotGate gate = new();
        ConnectionRunner runner = new(services.GetRequiredService<IServiceScopeFactory>(), gate);
        Task closing = Task.Run(() =>
            runner.RunAsync(
                (scope, admission) =>
                {
                    admission.Enter(1000001);
                    scope.GetRequiredService<Cleanup>();
                    return Task.CompletedTask;
                }
            )
        );
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(gate.TryEnter(1000001));
        }
        finally
        {
            release.Set();
        }
        await closing;
        Assert.True(gate.TryEnter(1000001));
    }

    private sealed class FailingSecondScope(IServiceScopeFactory scopes) : IServiceScopeFactory
    {
        private int _calls;

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                throw new InvalidOperationException("Simulated connection setup failure.");
            }
            return scopes.CreateScope();
        }
    }

    [Fact]
    public async Task FailedConnectionAllowsAnotherClientWhileHealthyClientStaysOpen()
    {
        using TestWorld world = new();
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        LoopbackServer server = new(
            new ConnectionRunner(
                new FailingSecondScope(world.Services.GetRequiredService<IServiceScopeFactory>()),
                world.Services.GetRequiredService<PilotGate>()
            )
        );
        Task listening = server.ListenAsync(listener, stop.Token);
        try
        {
            using TcpClient healthy = new();
            await healthy.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
            Assert.NotEmpty(await PacketStream.ReadAsync(healthy.GetStream(), stop.Token));
            using TcpClient failed = new();
            await failed.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
            Assert.Equal(0, await failed.GetStream().ReadAsync(new byte[1], stop.Token));
            using TcpClient next = new();
            await next.ConnectAsync((IPEndPoint)listener.LocalEndpoint, stop.Token);
            Assert.NotEmpty(await PacketStream.ReadAsync(next.GetStream(), stop.Token));
            // The first client's incomplete handshake is still served after the failed peer.
            Assert.Equal(0, healthy.Available);
            Assert.False(healthy.Client.Poll(0, SelectMode.SelectRead));
        }
        finally
        {
            await stop.CancelAsync();
            await listening;
        }
    }
}
