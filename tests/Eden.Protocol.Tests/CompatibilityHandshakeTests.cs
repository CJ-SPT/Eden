using System.Net;
using System.Net.Sockets;

namespace Eden.Protocol.Tests;

public sealed class CompatibilityHandshakeTests
{
    // Serialized separately from the server encoder. The real 3662168 client confirmed
    // the six-field reply and QC bytes on the loopback smoke test.
    private const string ClientVersion = "7E00000000140604E899020005F001080AC3F5285C8F0238400458E13700130A5632342E303140636370";
    private const string QueueCheck = "7E000000002C0113025143";
    private const string ServerGreeting = "7E00000000140704E899020005F001080AC3F5285C8F0238400458E13700130A5632342E3031406363701307556E6B6E6F776E";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NegotiatesOnlyThePinnedClientAndQueueCheck(bool wrongBuild, bool wrongCommand)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        Task exchange = CompatibilityHandshake.ServeAsync(accepted.GetStream(), _ => { }, timeout.Token);
        NetworkStream stream = client.GetStream();

        Assert.Equal(Convert.FromHexString(ServerGreeting), await PacketStream.ReadAsync(stream, timeout.Token));
        string version = wrongBuild ? ClientVersion.Replace("58E13700", "59E13700") : ClientVersion;
        await PacketStream.WriteAsync(stream, Convert.FromHexString(version), timeout.Token);
        if (!wrongBuild)
        {
            string command = wrongCommand ? QueueCheck.Replace("5143", "564B") : QueueCheck;
            await PacketStream.WriteAsync(stream, Convert.FromHexString(command), timeout.Token);
        }

        if (wrongBuild || wrongCommand)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => exchange);
            return;
        }

        Assert.Equal(Convert.FromHexString("7E0000000008"), await PacketStream.ReadAsync(stream, timeout.Token));
        await exchange;
    }
}
