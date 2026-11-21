using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Eden.Gameplay.Docking;
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
            await using var api = LauncherApi.Create(options, store, identity, TimeProvider.System);
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
        await using var api = LauncherApi.Create(options, store, identity, TimeProvider.System);
        await api.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{options.ApiPort}/api/v1/") };
        var server = await client.GetFromJsonAsync<ServerReply>("server");
        Assert.Equal(1, server!.ApiVersion);
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

    [Fact]
    public async Task AccountEndpointsWaitForWorldReadiness()
    {
        var options = new LauncherOptions { ApiPort = AvailablePort() };
        LauncherReadiness readiness = new();
        const string setupUrl = "http://127.0.0.1:26003/world/setup";
        readiness.RequireSetup(setupUrl);
        await using var api = LauncherApi.Create(options, readiness, TimeProvider.System);
        await api.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{options.ApiPort}/api/v1/") };

        var server = await client.GetFromJsonAsync<ServerReply>("server");
        Assert.Equal(LauncherReadiness.SetupRequired, server!.WorldStatus);
        Assert.Equal(setupUrl, server.WorldSetupUrl);
        using var remote = new HttpRequestMessage(HttpMethod.Get, "server");
        remote.Headers.Host = "eden.test";
        using var remoteReply = await client.SendAsync(remote);
        Assert.Null((await remoteReply.Content.ReadFromJsonAsync<ServerReply>())!.WorldSetupUrl);

        foreach (string endpoint in new[] { "accounts", "sessions", "sessions/refresh", "launch-tickets" })
        {
            using var response = await client.PostAsJsonAsync(endpoint, new { });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("world_setup_required", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }
        readiness.StartWorld();
        Assert.Equal(LauncherReadiness.Starting, (await client.GetFromJsonAsync<ServerReply>("server"))!.WorldStatus);
        using var starting = await client.PostAsJsonAsync("sessions", new SignInRequest("Test", "secret", false));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, starting.StatusCode);
        Assert.Equal("world_starting", (await starting.Content.ReadFromJsonAsync<ApiError>())!.Code);

        using TestWorld world = new(liveWorld: true);
        var store = new LauncherStore(new WorldDatabase(world.Path), TimeProvider.System);
        store.Initialize();
        using var identity = new DevelopmentIdentity(TimeProvider.System, store, options);
        readiness.Open(new(store, identity, null, null, null));
        Assert.Equal(LauncherReadiness.Ready, (await client.GetFromJsonAsync<ServerReply>("server"))!.WorldStatus);
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
        await using var api = LauncherApi.Create(options, store, identity, TimeProvider.System);
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
