using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Eden.Gameplay.Chat;
using Eden.Protocol.Chat;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Chat;

public sealed class XmppChatTests
{
    [Fact]
    public async Task InitialAvailabilityUsesTheAuthenticatedResourceAndDoesNotEchoClientSuppliedIdentity()
    {
        using TestWorld world = new();
        var runtime = world.Services.GetRequiredService<ChatRuntime>();
        var certificate = world.Services.GetRequiredService<ChatCertificate>();
        await using var server = world.Services.GetRequiredService<ChatServer>();
        await server.StartAsync(new(IPAddress.Loopback, 0));
        Guid session = Guid.NewGuid();
        runtime.Update(session, new(90000001, "Pilot", 1000044, 30000142, 0));
        await using var client = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            90000001,
            runtime.IssueToken(session)
        );
        await client.SendAsync(
            "<presence from='90000002@eden.chat/forged' id='not-a-join'>" + "<eve_user_data corpid='1' role='999999'/></presence>"
        );
        var available = await client.ReadAsync();
        Assert.Equal("presence", available.Name.LocalName);
        Assert.Equal("90000001@eden.chat/eveclient", (string?)available.Attribute("from"));
        Assert.Equal("90000001@eden.chat/eveclient", (string?)available.Attribute("to"));
        Assert.Null(available.Attribute("id"));
        Assert.Empty(available.Elements());
        Assert.Equal(1000044, runtime.User(90000001)!.CorporationId);
        Assert.Equal(0, runtime.User(90000001)!.Roles);
    }

    [Fact]
    public async Task SilentPilotsCompleteTheNativeReadyHandshakeAndSeeLocalBeforeAnyMessage()
    {
        using TestWorld world = new();
        var runtime = world.Services.GetRequiredService<ChatRuntime>();
        var certificate = world.Services.GetRequiredService<ChatCertificate>();
        await using var server = world.Services.GetRequiredService<ChatServer>();
        await server.StartAsync(new(IPAddress.Loopback, 0));
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid();
        runtime.Update(a, new(90000001, "First silent pilot", 1000044, 30000142, 0));
        runtime.Update(b, new(90000002, "Second silent pilot", 1000044, 30000142, 0));
        await using var first = await NativeClient.ConnectAsync(server.Endpoint!, certificate.Certificate, 90000001, runtime.IssueToken(a));
        await first.JoinAsync("local_30000142", "first-ready");
        await first.UntilAsync(e => (string?)e.Attribute("id") == "first-ready");
        Assert.Equal(new long[] { 90000001 }, RosterIds((await first.UntilAsync(IsRoster))[^1]));
        await using var second = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            90000002,
            runtime.IssueToken(b)
        );
        await second.JoinAsync("local_30000142", "second-ready");
        await second.UntilAsync(e => (string?)e.Attribute("id") == "second-ready");
        Assert.Equal(new long[] { 90000001, 90000002 }, RosterIds((await second.UntilAsync(IsRoster))[^1]));
        await first.UntilAsync(e => IsControl(e, "join", 90000002));
        await second.MessageAsync("local_30000142", "first-message", "hello");
        foreach (var client in new[] { first, second })
        {
            var received = await client.UntilAsync(e => (string?)e.Attribute("id") == "first-message");
            Assert.DoesNotContain(received, e => (string?)e.Attribute("type") == "error");
            Assert.Single(received, e => (string?)e.Attribute("id") == "first-message");
        }
    }

    [Theory]
    [InlineData("local_30000142")]
    [InlineData("corp_1000044")]
    public async Task FirstMessageRacingTheInitialJoinIsDeliveredOnceAfterRosterInitialization(string channel)
    {
        using TestWorld world = new();
        var runtime = world.Services.GetRequiredService<ChatRuntime>();
        var certificate = world.Services.GetRequiredService<ChatCertificate>();
        await using var server = world.Services.GetRequiredService<ChatServer>();
        await server.StartAsync(new(IPAddress.Loopback, 0));
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid();
        runtime.Update(a, new(90000001, "First", 1000044, 30000142, 0));
        runtime.Update(b, new(90000002, "Second", 1000044, 30000142, 0));
        await using var first = await NativeClient.ConnectAsync(server.Endpoint!, certificate.Certificate, 90000001, runtime.IssueToken(a));
        await using var second = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            90000002,
            runtime.IssueToken(b)
        );
        await second.JoinAsync(channel, "second-ready");
        await second.UntilAsync(e => (string?)e.Attribute("id") == "second-ready");
        await first.MessageAsync(channel, "early-message", "hello");
        await first.JoinAsync(channel, "first-ready");
        var received = await first.UntilAsync(e => (string?)e.Attribute("id") == "early-message");
        Assert.DoesNotContain(received, e => (string?)e.Attribute("type") == "error");
        Assert.Single(received, e => (string?)e.Attribute("id") == "first-ready");
        Assert.Single(received, e => (string?)e.Attribute("id") == "early-message");
        if (channel.StartsWith("local_", StringComparison.Ordinal))
        {
            var roster = Assert.Single(received, IsRoster);
            Assert.Equal(new long[] { 90000001, 90000002 }, RosterIds(roster));
            Assert.True(received.ToList().FindIndex(IsRoster) < received.Count - 1);
        }
        var peer = (await second.UntilAsync(e => (string?)e.Attribute("id") == "early-message"))[^1];
        Assert.Equal("hello", peer.Elements().Single(e => e.Name.LocalName == "body").Value);
        await first.JoinAsync(channel, "retry");
        var retried = await first.UntilAsync(e => (string?)e.Attribute("id") == "retry");
        Assert.DoesNotContain(retried, e => (string?)e.Attribute("id") == "early-message");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemTransitionRevokesOldLocalWhileKeepingCorporationChatAndJoiningTheDestination(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        var runtime = world.Services.GetRequiredService<ChatRuntime>();
        var certificate = world.Services.GetRequiredService<ChatCertificate>();
        await using var server = world.Services.GetRequiredService<ChatServer>();
        await server.StartAsync(new(IPAddress.Loopback, 0));
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid(),
            c = Guid.NewGuid();
        ChatPilot pilotA = new(90000001, "First", 1000044, 30000142, 0);
        ChatPilot pilotB = new(90000002, "Second", 1000044, 30000142, 0);
        ChatPilot pilotC = new(90000003, "Destination", 1000044, 30000144, 0);
        runtime.Update(a, pilotA);
        runtime.Update(b, pilotB);
        runtime.Update(c, pilotC);
        await using var first = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            pilotA.Id,
            runtime.IssueToken(a)
        );
        await using var second = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            pilotB.Id,
            runtime.IssueToken(b)
        );
        await using var third = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            pilotC.Id,
            runtime.IssueToken(c)
        );
        await first.JoinAsync("local_30000142", "a-local");
        await first.UntilAsync(e => (string?)e.Attribute("id") == "a-local");
        await second.JoinAsync("local_30000142", "b-local");
        await second.UntilAsync(e => (string?)e.Attribute("id") == "b-local");
        await first.UntilAsync(e => IsControl(e, "join", pilotB.Id));
        await third.JoinAsync("local_30000144", "c-local");
        await third.UntilAsync(e => (string?)e.Attribute("id") == "c-local");
        await first.JoinAsync("corp_1000044", "a-corp");
        await first.UntilAsync(e => (string?)e.Attribute("id") == "a-corp");
        await second.JoinAsync("corp_1000044", "b-corp");
        await second.UntilAsync(e => (string?)e.Attribute("id") == "b-corp");
        runtime.Update(b, pilotB with { SystemId = 30000144 });
        await first.UntilAsync(e => IsControl(e, "leave", pilotB.Id));
        await second.MessageAsync("local_30000142", "forbidden", "old Local injection");
        Assert.Equal("error", (string?)(await second.UntilAsync(e => (string?)e.Attribute("id") == "forbidden"))[^1].Attribute("type"));
        await second.JoinAsync("local_30000144", "b-destination");
        await second.UntilAsync(e => (string?)e.Attribute("id") == "b-destination");
        await third.UntilAsync(e => IsControl(e, "join", pilotB.Id));
        await second.MessageAsync("local_30000144", "destination-message", "new Local");
        await third.UntilAsync(e => (string?)e.Attribute("id") == "destination-message");
        await second.MessageAsync("corp_1000044", "corporation-message", "corp still works across systems");
        await first.UntilAsync(e => (string?)e.Attribute("id") == "corporation-message");
    }

    [Fact]
    public async Task PrivateInvitationAndRoomInformationUseNativeAddressesAndOccupantCounts()
    {
        using TestWorld world = new();
        var runtime = world.Services.GetRequiredService<ChatRuntime>();
        var certificate = world.Services.GetRequiredService<ChatCertificate>();
        await using var server = world.Services.GetRequiredService<ChatServer>();
        await server.StartAsync(new(IPAddress.Loopback, 0));
        Guid a = Guid.NewGuid(),
            b = Guid.NewGuid();
        runtime.Update(a, new(90000001, "First", 1000044, 30000142, 0));
        runtime.Update(b, new(90000002, "Second", 1000044, 30000144, 0));
        await using var first = await NativeClient.ConnectAsync(server.Endpoint!, certificate.Certificate, 90000001, runtime.IssueToken(a));
        await using var second = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            90000002,
            runtime.IssueToken(b)
        );
        string channel = XmppWire.Channel(runtime.CreatePrivate(a, 90000002));
        await first.JoinAsync(channel, "private-a");
        await first.UntilAsync(e => (string?)e.Attribute("id") == "private-a");
        await first.SendAsync(
            "<message to='"
                + channel
                + "@conference.eden.chat'><x xmlns='http://jabber.org/protocol/muc#user'>"
                + "<invite to='90000002@eden.chat'><reason>Private chat</reason></invite></x></message>"
        );
        var invite = (await second.UntilAsync(e => e.Descendants().Any(d => d.Name.LocalName == "invite")))[^1];
        Assert.Equal("90000001@eden.chat", invite.Descendants().Single(d => d.Name.LocalName == "invite").Attribute("from")!.Value);
        await second.JoinAsync(channel, "private-b");
        await second.UntilAsync(e => (string?)e.Attribute("id") == "private-b");
        await second.SendAsync(
            "<iq type='get' id='info' to='"
                + channel
                + "@conference.eden.chat'>"
                + "<query xmlns='http://jabber.org/protocol/disco#info'/></iq>"
        );
        var info = (await second.UntilAsync(e => (string?)e.Attribute("id") == "info"))[^1];
        Assert.Equal("2", info.Descendants().Single(e => (string?)e.Attribute("var") == "muc#roominfo_occupants").Value);
        await first.MessageAsync(channel, "private-message", "private across systems");
        await second.UntilAsync(e => (string?)e.Attribute("id") == "private-message");
    }

    [Fact]
    public async Task TwoSelectedPilotsExchangeUnicodeLocalMessagesAndSeeConnectionAndLogoffDeparturesOverTls()
    {
        using TestWorld world = new();
        var runtime = world.Services.GetRequiredService<ChatRuntime>();
        var certificate = world.Services.GetRequiredService<ChatCertificate>();
        await using var server = world.Services.GetRequiredService<ChatServer>();
        await server.StartAsync(new(IPAddress.Loopback, 0));
        using var scopeA = world.Services.CreateScope();
        using var scopeB = world.Services.CreateScope();
        var pilotA = world.Store.FindPilot(1000001)!;
        var pilotB = world.Store.FindPilot(1000002)!;
        using PilotCallContext a = new(
            1000001,
            pilotA.Account,
            [pilotA],
            p => new(world.Store, p, TimeProvider.System, new(), []),
            42,
            SignedSettings.Empty
        );
        using PilotCallContext b = new(
            1000002,
            pilotB.Account,
            [pilotB],
            p => new(world.Store, p, TimeProvider.System, new(), []),
            43,
            SignedSettings.Empty
        );
        var routerA = scopeA.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Same(
            PilotCallResult.Unsupported,
            routerA.Call(a, "chatAuthenticationService", "GetAuthenticationToken", Array.Empty<object?>(), _ => { })
        );
        a.Select(pilotA);
        b.Select(pilotB);
        var routerB = scopeB.ServiceProvider.GetRequiredService<PilotCallRouter>();
        string Token(PilotCallRouter router, PilotCallContext context) =>
            BlueMarshal.Text(
                Assert
                    .IsType<PilotCallResult.Success>(
                        router.Call(context, "chatAuthenticationService", "GetAuthenticationToken", Array.Empty<object?>(), _ => { })
                    )
                    .Reply.ToBlue()
            );
        await using var first = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            pilotA.PilotId,
            Token(routerA, a)
        );
        await using var second = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            pilotB.PilotId,
            Token(routerB, b)
        );
        string channel = "local_" + pilotA.SystemId;
        await first.JoinAsync(channel, "join-a");
        await first.UntilAsync(e => (string?)e.Attribute("id") == "join-a");
        await second.JoinAsync(channel, "join-b");
        var initialB = await second.UntilAsync(e => (string?)e.Attribute("id") == "join-b");
        Assert.Single(initialB, e => e.Name.LocalName == "presence" && (string?)e.Attribute("id") == "join-b");
        var rosterB = (await second.UntilAsync(IsRoster))[^1];
        Assert.Equal(new[] { pilotA.PilotId, pilotB.PilotId }, RosterIds(rosterB));
        await first.UntilAsync(e => IsControl(e, "join", pilotB.PilotId));
        const string message = "こんにちは 🚀 <b>two pilots</b> & Local";
        await first.MessageAsync(channel, "a-message", message);
        foreach (var client in new[] { first, second })
        {
            var received = (await client.UntilAsync(e => (string?)e.Attribute("id") == "a-message"))[^1];
            Assert.Equal(message, received.Elements().Single(e => e.Name.LocalName == "body").Value);
            Assert.Equal(channel + "@conference.eden.chat/" + pilotA.PilotId, (string?)received.Attribute("from"));
        }
        await second.MessageAsync(channel, "b-message", "Reply from the other pilot");
        await first.UntilAsync(e => (string?)e.Attribute("id") == "b-message");
        await second.UntilAsync(e => (string?)e.Attribute("id") == "b-message");
        await second.DisposeAsync();
        await first.UntilAsync(e => IsControl(e, "leave", pilotB.PilotId));
        scopeB.ServiceProvider.GetRequiredService<ChatHandler>().Dispose();
        await using var reconnected = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            pilotB.PilotId,
            Token(routerB, b)
        );
        await reconnected.JoinAsync(channel, "join-again");
        await reconnected.UntilAsync(e => (string?)e.Attribute("id") == "join-again");
        await first.UntilAsync(e => IsControl(e, "join", pilotB.PilotId));
        scopeB.ServiceProvider.GetRequiredService<ChatHandler>().Dispose();
        await first.UntilAsync(e => IsControl(e, "leave", pilotB.PilotId));
        Assert.Null(runtime.User(pilotB.PilotId));
        Assert.Empty(a.Notifications);
    }

    private static bool IsControl(XElement stanza, string command, long pilotId)
    {
        if (((string?)stanza.Attribute("from"))?.EndsWith("/admin", StringComparison.Ordinal) != true)
        {
            return false;
        }
        using var json = JsonDocument.Parse(stanza.Elements().Single(e => e.Name.LocalName == "body").Value);
        return json.RootElement.GetProperty("cmd").GetString() == command && json.RootElement.GetProperty("charid").GetInt64() == pilotId;
    }

    private static bool IsRoster(XElement stanza) =>
        ((string?)stanza.Attribute("from"))?.EndsWith("/viking", StringComparison.Ordinal) == true;

    private static long[] RosterIds(XElement stanza) =>
        stanza
            .Elements()
            .Select(user =>
            {
                using var info = JsonDocument.Parse(user.Attribute("info")!.Value);
                return info.RootElement[0].GetInt64();
            })
            .ToArray();

    [Fact]
    public async Task ForgedLocalNickAndUserDataWritesAreRejectedWithoutDeliveringMessages()
    {
        using TestWorld world = new();
        var runtime = world.Services.GetRequiredService<ChatRuntime>();
        var certificate = world.Services.GetRequiredService<ChatCertificate>();
        await using var server = world.Services.GetRequiredService<ChatServer>();
        await server.StartAsync(new(IPAddress.Loopback, 0));
        Guid session = Guid.NewGuid();
        runtime.Update(session, new(90000001, "Pilot", 1000044, 30000142, 0));
        await using var client = await NativeClient.ConnectAsync(
            server.Endpoint!,
            certificate.Certificate,
            90000001,
            runtime.IssueToken(session)
        );
        await client.SendAsync("<presence to='local_30000144@conference.eden.chat/90000001' id='wrong-system'/>");
        Assert.Equal("error", (string?)(await client.UntilAsync(e => (string?)e.Attribute("id") == "wrong-system"))[^1].Attribute("type"));
        await client.SendAsync("<presence to='local_30000142@conference.eden.chat/90000002' id='wrong-nick'/>");
        Assert.Equal("error", (string?)(await client.UntilAsync(e => (string?)e.Attribute("id") == "wrong-nick"))[^1].Attribute("type"));
        await client.SendAsync(
            "<iq type='set' id='forged-data'><query xmlns='urn:xmpp:eve_user_data' jid='90000001@eden.chat'>"
                + "<eve_user_data corpid='1' role='999999'/></query></iq>"
        );
        Assert.Equal("error", (string?)(await client.UntilAsync(e => (string?)e.Attribute("id") == "forged-data"))[^1].Attribute("type"));
        await client.JoinAsync("local_30000142", "allowed");
        var presence = (await client.UntilAsync(e => (string?)e.Attribute("id") == "allowed"))[^1];
        Assert.Equal("1000044", presence.Descendants().Single(e => e.Name.LocalName == "eve_user_data").Attribute("corpid")!.Value);
        Assert.Equal("0", presence.Descendants().Single(e => e.Name.LocalName == "eve_user_data").Attribute("role")!.Value);
        runtime.Update(session, null);
    }

    internal sealed class NativeClient : IAsyncDisposable
    {
        private readonly TcpClient _client = new();
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));
        private SslStream _stream = null!;
        private XmppStreamReader _reader = null!;
        private long _pilot;
        private bool _disposed;

        public static async Task<NativeClient> ConnectAsync(IPEndPoint endpoint, X509Certificate2 root, long pilot, string token)
        {
            NativeClient client = new() { _pilot = pilot };
            try
            {
                await client._client.ConnectAsync(endpoint, client._timeout.Token);
                client._stream = new(client._client.GetStream());
                await client._stream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = "eden.chat",
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateChainPolicy = new X509ChainPolicy
                        {
                            TrustMode = X509ChainTrustMode.CustomRootTrust,
                            CustomTrustStore = { root },
                            RevocationMode = X509RevocationMode.NoCheck,
                        },
                    },
                    client._timeout.Token
                );
                client._reader = new(client._stream);
                await client.OpenAsync();
                string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + pilot + "\0" + token));
                await client.SendAsync("<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>" + auth + "</auth>");
                Assert.Equal("success", (await client.ReadAsync()).Name.LocalName);
                await client.OpenAsync();
                await client.SendAsync(
                    "<iq type='set' id='bind'><bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'><resource>eveclient</resource></bind></iq>"
                );
                Assert.Equal("result", (string?)(await client.ReadAsync()).Attribute("type"));
                await client.SendAsync("<iq type='set' id='session'><session xmlns='urn:ietf:params:xml:ns:xmpp-session'/></iq>");
                Assert.Equal("result", (string?)(await client.ReadAsync()).Attribute("type"));
                await client.SendAsync("<presence><show/></presence>");
                // Build 3662168 handles the session IQ callback separately. Its
                // handle_logged_in hook fires only on the next non-IQ stanza.
                // Channel joins must wait for this initial self-presence.
                var available = await client.ReadAsync();
                Assert.Equal("presence", available.Name.LocalName);
                Assert.Equal(pilot + "@eden.chat/eveclient", (string?)available.Attribute("from"));
                Assert.Equal(pilot + "@eden.chat/eveclient", (string?)available.Attribute("to"));
                Assert.Null(available.Attribute("type"));
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        private async Task OpenAsync()
        {
            await SendAsync(
                "<stream:stream to='eden.chat' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' version='1.0'>"
            );
            Assert.Equal("stream", (await ReadAsync()).Name.LocalName);
            Assert.Equal("features", (await ReadAsync()).Name.LocalName);
        }

        public Task JoinAsync(string channel, string id) =>
            SendAsync(
                XmppWire.Xml(
                    new XElement(
                        "presence",
                        new XAttribute("to", channel + "@conference.eden.chat/" + _pilot),
                        new XAttribute("id", id),
                        new XElement(XName.Get("x", "http://jabber.org/protocol/muc"))
                    )
                )
            );

        public Task MessageAsync(string channel, string id, string text) =>
            SendAsync(
                XmppWire.Xml(
                    new XElement(
                        "message",
                        new XAttribute("to", channel + "@conference.eden.chat"),
                        new XAttribute("id", id),
                        new XAttribute("type", "groupchat"),
                        new XElement("body", text)
                    )
                )
            );

        public async Task SendAsync(string text) => await _stream.WriteAsync(Encoding.UTF8.GetBytes(text), _timeout.Token);

        public async Task<XElement> ReadAsync() => await _reader.ReadAsync(_timeout.Token) ?? throw new IOException("Chat stream ended.");

        public async Task<IReadOnlyList<XElement>> UntilAsync(Func<XElement, bool> predicate)
        {
            List<XElement> read = [];
            while (read.Count < 50)
            {
                var value = await ReadAsync();
                read.Add(value);
                if (predicate(value))
                {
                    return read;
                }
            }
            throw new InvalidDataException("Expected chat stanza was not received.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _reader?.Dispose();
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
            }
            _client.Dispose();
            _timeout.Dispose();
        }
    }
}
