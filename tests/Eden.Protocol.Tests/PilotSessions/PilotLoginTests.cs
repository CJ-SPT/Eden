using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PilotLoginTests
{
    private sealed class FixtureIdentity(RSA key) : ILoginIdentity
    {
        public RSA Key => key;

        public long? ValidateTicket(string ticket) => ticket == "fixture" ? 1000001 : null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiresBothLocalTicketAndChallengeProof(bool validTicket)
    {
        // Cold seed preparation is unrelated to the five-second wire exchange.
        using TestWorld world = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using RSA key = RSA.Create(2048);
        using Aes aes = Aes.Create();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using TcpClient server = await listener.AcceptTcpClientAsync(timeout.Token);
        NetworkStream wire = client.GetStream();
        List<string> messages = [];
        using var scope = world.Services.CreateScope();
        PilotLogin login = new(new FixtureIdentity(key), scope.ServiceProvider.GetRequiredService<IPilotSession>());
        byte[] request = BlueMarshal.Encode(new object?[] { null, "VK", Array.Empty<byte>() });
        Task exchange = login.RunAsync(server.GetStream(), request, messages.Add, timeout.Token);
        Dictionary<string, object?> keys = new()
        {
            ["crypting_sessionkey"] = key.Encrypt(aes.Key, RSAEncryptionPadding.Pkcs1),
            ["crypting_sessioniv"] = key.Encrypt(aes.IV, RSAEncryptionPadding.Pkcs1),
        };
        await PacketStream.WriteAsync(wire, BlueMarshal.Encode(new object?[] { "test", keys }), timeout.Token);
        Assert.Equal("OK CC", BlueMarshal.Text(BlueMarshal.Decode(await PacketStream.ReadAsync(wire, timeout.Token))));

        byte[] challenge = RandomNumberGenerator.GetBytes(64);
        var credentials = new Dictionary<string, object?> { ["user_sso_token"] = validTicket ? "fixture" : "invalid" };
        await SendEncrypted(new object?[] { challenge, credentials });
        if (validTicket)
        {
            Assert.Equal(2L, await ReadEncrypted());
            object?[] reply = BlueRead.Tuple(await ReadEncrypted(), 4);
            object?[] signed = BlueRead.Tuple(reply[1], 2);
            Assert.Equal(PortraitClient.Expression, BlueRead.Bytes(signed[0]));
            byte[] digest = SHA256.HashData(BlueMarshal.Encode(new object?[] { signed[0] }));
            Assert.True(key.VerifyData(digest, BlueRead.Bytes(signed[1]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            await SendEncrypted(new object?[] { new byte[32], "", null });
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => exchange);
        Assert.DoesNotContain(messages, line => line.StartsWith("Authenticated", StringComparison.Ordinal));

        async Task SendEncrypted(object? value)
        {
            await PacketStream.WriteAsync(wire, aes.EncryptCbc(BlueMarshal.Encode(value), aes.IV), timeout.Token);
        }

        async Task<object?> ReadEncrypted()
        {
            return BlueMarshal.Decode(aes.DecryptCbc(await PacketStream.ReadAsync(wire, timeout.Token), aes.IV));
        }
    }
}
