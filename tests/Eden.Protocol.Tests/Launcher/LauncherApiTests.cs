using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Eden.Gameplay.Docking;
using Eden.Server.Authentication;
using Eden.Server.Launcher;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Launcher;

public sealed class LauncherApiTests
{
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
