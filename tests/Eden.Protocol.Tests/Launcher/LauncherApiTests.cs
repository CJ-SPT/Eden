using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Eden.Gameplay.Docking;
using Eden.Protocol.Tests.Administration;
using Eden.Server.Authentication;
using Eden.Server.Launcher;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Launcher;

public sealed class LauncherApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpsPublishesOnlyTheConfiguredPublicCaCertificate(bool publish)
    {
        using TestWorld world = new();
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Eden Test CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var now = DateTimeOffset.UtcNow;
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName("localhost");
        leafRequest.CertificateExtensions.Add(subjectAlternativeName.Build());
        using var leaf = leafRequest.Create(root, now.AddDays(-1), now.AddDays(1), [1]);
        using var serverCertificate = leaf.CopyWithPrivateKey(leafKey);

        var directory = Directory.CreateTempSubdirectory("eden-ca-api-");
        try
        {
            string caPath = Path.Combine(directory.FullName, "ca.pem");
            string pfxPath = Path.Combine(directory.FullName, "server.pfx");
            File.WriteAllText(caPath, root.ExportCertificatePem());
            File.WriteAllBytes(pfxPath, serverCertificate.Export(X509ContentType.Pfx));
            var options = new LauncherOptions
            {
                ApiPort = AvailablePort(),
                CertificatePath = pfxPath,
                CertificateAuthorityPath = publish ? caPath : null,
            };
            var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
            using var identity = new DevelopmentIdentity(TimeProvider.System, store, options);
            await using var api = LauncherApi.Create(options, store, identity);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await api.StartAsync(timeout.Token);
            using var client = new HttpClient(
                new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Thumbprint == leaf.Thumbprint,
                }
            );
            using var response = await client.GetAsync($"https://localhost:{options.ApiPort}/api/v1/ca-certificate", timeout.Token);
            Assert.Equal(publish ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            if (publish)
            {
                Assert.Equal("application/pkix-cert", response.Content.Headers.ContentType!.MediaType);
                Assert.Equal(root.RawData, await response.Content.ReadAsByteArrayAsync(timeout.Token));
            }
            await api.StopAsync(timeout.Token);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HttpContractRegistersSignsInAndRevokesWithoutCreatingPilots()
    {
        const string password = "secret";
        using TestWorld world = new();
        var options = new LauncherOptions { ApiPort = AvailablePort() };
        var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        store.Initialize();
        using var identity = new DevelopmentIdentity(TimeProvider.System, store, options);
        await using var api = LauncherApi.Create(options, store, identity);
        await api.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{options.ApiPort}/api/v1/") };
        var server = await client.GetFromJsonAsync<ServerReply>("server");
        Assert.Equal(1, server!.ApiVersion);
        Assert.Null(server.AdministrationUrl);
        using var registration = await client.PostAsJsonAsync("accounts", new AccountRequest("Http Pilot", password));
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var account = await registration.Content.ReadFromJsonAsync<AccountReply>();
        Assert.Empty(world.Store.ListPilots(account!.AccountId));
        Assert.Equal(AccountCloneGrade.Omega, world.Store.ReadAccount(account.AccountId).CloneGrade);
        using var signIn = await client.PostAsJsonAsync("sessions", new SignInRequest("Http Pilot", password, true));
        var session = await signIn.Content.ReadFromJsonAsync<SessionReply>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.AccessToken);
        using var manifest = await client.PostAsJsonAsync("launch-tickets", new LaunchRequest(3662168, "bad"));
        Assert.Equal(HttpStatusCode.BadRequest, manifest.StatusCode);
        using var signOut = await client.DeleteAsync("sessions/current");
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        using var refresh = await client.PostAsJsonAsync("sessions/refresh", new RefreshRequest(session.RefreshToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("no-store", signIn.Headers.CacheControl!.ToString());
        await api.StopAsync();
    }

    [PinnedSkillClientFact]
    public async Task LaunchReplyReportsTheExactTicketDeadlineNearSessionExpiry()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        LauncherOptions options = new() { ApiPort = AvailablePort() };
        LauncherStore store = new(new(world.Path), clock);
        const string name = "Expiring Launch Pilot";
        const string password = "launch test password";
        var account = store.Register(new(name, password));
        var session = store.SignIn(new(name, password, false));
        using var identity = new DevelopmentIdentity(clock, store, options);
        await using var api = LauncherApi.Create(options, store, identity);
        await api.StartAsync();
        using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{options.ApiPort}/api/v1/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        string root = Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!;
        byte[] manifest = await File.ReadAllBytesAsync(Path.Combine(root, "tq", "manifest.dat"));
        Convert.FromHexString(ClientManifest.OriginalCodeHash).CopyTo(manifest, ClientManifest.CodeHashOffset);
        LaunchRequest request = new(3662168, Convert.ToBase64String(ClientManifest.ReadOriginalPrefix(manifest)));
        clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(session.ExpiresAt - 1);

        var first = await Launch();
        var second = await Launch();

        Assert.Equal(session.ExpiresAt, first.ExpiresAt);
        Assert.Equal(session.ExpiresAt, second.ExpiresAt);
        Assert.Equal(account.AccountId, await store.ConsumeTicketAsync(first.Ticket));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Assert.Null(await store.ConsumeTicketAsync(second.Ticket));
        await api.StopAsync();

        async Task<LaunchReply> Launch()
        {
            using var response = await client.PostAsJsonAsync("launch-tickets", request);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<LaunchReply>())!;
        }
    }

    [Fact]
    public async Task AccountEndpointsWaitForWorldReadiness()
    {
        var options = new LauncherOptions { ApiPort = AvailablePort() };
        LauncherReadiness readiness = new();
        const string administrationUrl = "http://127.0.0.1:26003";
        readiness.RequireSetup(administrationUrl);
        await using var api = LauncherApi.Create(options, readiness);
        await api.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{options.ApiPort}/api/v1/") };

        var server = await client.GetFromJsonAsync<ServerReply>("server");
        Assert.Equal(LauncherReadiness.SetupRequired, server!.WorldStatus);
        Assert.Equal(administrationUrl + "/world/setup", server.WorldSetupUrl);
        Assert.Equal(administrationUrl, server.AdministrationUrl);
        using var remote = new HttpRequestMessage(HttpMethod.Get, "server");
        remote.Headers.Host = "eden.test";
        using var remoteReply = await client.SendAsync(remote);
        var remoteServer = await remoteReply.Content.ReadFromJsonAsync<ServerReply>();
        Assert.Null(remoteServer!.WorldSetupUrl);
        Assert.Null(remoteServer.AdministrationUrl);

        foreach (string endpoint in new[] { "accounts", "sessions", "sessions/refresh", "launch-tickets" })
        {
            using var response = await client.PostAsJsonAsync(endpoint, new { });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("world_setup_required", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }
        readiness.StartWorld();
        var startingServer = await client.GetFromJsonAsync<ServerReply>("server");
        Assert.Equal(LauncherReadiness.Starting, startingServer!.WorldStatus);
        Assert.Null(startingServer.AdministrationUrl);
        using var starting = await client.PostAsJsonAsync("sessions", new SignInRequest("Test", "secret", false));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, starting.StatusCode);
        Assert.Equal("world_starting", (await starting.Content.ReadFromJsonAsync<ApiError>())!.Code);

        using TestWorld world = new(liveWorld: true);
        var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        store.Initialize();
        using var identity = new DevelopmentIdentity(TimeProvider.System, store, options);
        LauncherWorld launcherWorld = new(store, identity, null, null, null);
        readiness.Open(launcherWorld, administrationUrl);
        var readyServer = await client.GetFromJsonAsync<ServerReply>("server");
        Assert.Equal(LauncherReadiness.Ready, readyServer!.WorldStatus);
        Assert.Equal(administrationUrl, readyServer.AdministrationUrl);
        Assert.Null(readyServer.WorldSetupUrl);

        client.DefaultRequestHeaders.Host = "eden.test";
        Assert.Null((await client.GetFromJsonAsync<ServerReply>("server"))!.AdministrationUrl);
        const string publicAdministrationUrl = "https://eden.test:26003";
        readiness.Open(launcherWorld, publicAdministrationUrl);
        Assert.Equal(publicAdministrationUrl, (await client.GetFromJsonAsync<ServerReply>("server"))!.AdministrationUrl);
        using var registration = await client.PostAsJsonAsync("accounts", new AccountRequest("Test", "secret"));
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        using var signIn = await client.PostAsJsonAsync("sessions", new SignInRequest("Test", "secret", false));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        await api.StopAsync();
    }

    [Fact]
    public async Task RepeatedSignInAttemptsAreThrottled()
    {
        using TestWorld world = new();
        var options = new LauncherOptions { ApiPort = AvailablePort() };
        var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        store.Initialize();
        using var identity = new DevelopmentIdentity(TimeProvider.System, store, options);
        await using var api = LauncherApi.Create(options, store, identity);
        await api.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{options.ApiPort}/api/v1/") };
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using var response = await client.PostAsJsonAsync("sessions", new SignInRequest("Unknown", "wrong", false));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var rejected = await client.PostAsJsonAsync("sessions", new SignInRequest("Unknown", "wrong", false));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        await api.StopAsync();
    }

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
