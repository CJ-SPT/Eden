using System.Net;
using System.Net.Sockets;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Protocol.Docking;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Transport;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Hosting;

public sealed class ServerServicesTests
{
    [Fact]
    public void SharesWorldAndGateButIsolatesPilotSessions()
    {
        using TestWorld world = new();
        using var first = world.Services.CreateScope();
        using var second = world.Services.CreateScope();
        var session = first.ServiceProvider.GetRequiredService<IPilotSession>();
        Assert.Same(session, first.ServiceProvider.GetRequiredService<PilotServices>());
        Assert.Contains(
            first.ServiceProvider.GetRequiredService<MiningHandler>(),
            first.ServiceProvider.GetServices<IModuleEffectSource>()
        );
        Assert.NotSame(session, second.ServiceProvider.GetRequiredService<IPilotSession>());
        Assert.Same(world.Store, first.ServiceProvider.GetRequiredService<IPilotLookup>());
        Assert.IsType<PilotHealthStore>(first.ServiceProvider.GetRequiredService<IPilotHealth>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<IPilotHealth>(), first.ServiceProvider.GetRequiredService<IPilotHealth>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<IPilotHealth>(), second.ServiceProvider.GetRequiredService<IPilotHealth>());
        Assert.Same(world.Store, second.ServiceProvider.GetRequiredService<IShipPositionStore>());
        Assert.Same(first.ServiceProvider.GetRequiredService<PilotGate>(), second.ServiceProvider.GetRequiredService<PilotGate>());
        Assert.Throws<InvalidOperationException>(() => world.Services.GetRequiredService<ClientConnection>());
    }

    [Fact]
    public async Task MalformedLoginDoesNotOccupyAnAccount()
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        using ConnectionAdmission admission = new(world.Services.GetRequiredService<PilotGate>());
        Task serving = scope.ServiceProvider.GetRequiredService<ClientConnection>().ServeAsync(accepted, admission, timeout.Token);
        var wire = client.GetStream();
        await PacketStream.ReadAsync(wire, timeout.Token);
        await PacketStream.WriteAsync(
            wire,
            Convert.FromHexString("7E00000000140604E899020005F001080AC3F5285C8F0238400458E13700130A5632342E303140636370"),
            timeout.Token
        );
        // An incomplete key exchange has no authenticated account.
        await PacketStream.WriteAsync(wire, BlueMarshal.Encode(new object?[] { null, "VK", null }), timeout.Token);
        var gate = world.Services.GetRequiredService<PilotGate>();
        await PacketStream.WriteAsync(wire, BlueMarshal.Encode(null), timeout.Token);
        await serving;
        Assert.True(gate.TryEnter(1000001));
        Assert.True(gate.TryEnter(1000002));
        Assert.False(gate.TryEnter(1000001));
        gate.Leave(1000001);
        Assert.True(gate.TryEnter(1000001));
        Assert.False(gate.TryEnter(1000002));
    }
}
