using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Eden.Gameplay.Mining;
using Eden.Server.Authentication;
using Eden.Server.Launcher;
using Eden.Server.Modding;
using Eden.World.Modding;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Administration;

public sealed class ModLauncherApiTests
{
    [PinnedSkillClientFact]
    public async Task V2RequiresExactModSetBeforeIssuingTicketsAndAuthenticatesArchiveDownloads()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        var store = new ModStore(database);
        string client = Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!;
        string archive = Path.Combine(Path.GetDirectoryName(world.Path)!, "AutoMining.edenmod");
        Assert.True(await ModCommands.TryRun(["--mod-export-automining", archive], database));
        store.Enable(store.Install(archive), true);

        ModPublisher publisher = new(database, store);
        string root = client;
        if (!OperatingSystem.IsWindows() || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDEN_PYTHON2")))
        {
            root = Path.Combine(Path.GetDirectoryName(world.Path)!, "client-input");
            Directory.CreateDirectory(root);
            string original = Path.Combine(client, ".eden-original-code.ccp");
            string input = Path.Combine(root, ".eden-original-code.ccp");
            await File.WriteAllTextAsync(input, "Wrong client archive");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => publisher.Build(root));
            Assert.Contains("Select an unchanged build-3662168", error.Message);
            Assert.Null(store.Status().Pending);
            File.Copy(File.Exists(original) ? original : Path.Combine(client, "tq", "code.ccp"), input, overwrite: true);
        }
        var set = await publisher.Build(root);
        Assert.True(store.BeginActivation());
        store.CompleteActivation();
        using TcpListener portProbe = new(IPAddress.Loopback, 0);
        portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        LauncherOptions options = new() { ApiPort = port };
        LauncherStore accounts = new(database, TimeProvider.System);
        accounts.Initialize();
        accounts.Register(new("Mod test pilot", "mod test password"));
        var session = accounts.SignIn(new("Mod test pilot", "mod test password", false));
        using var identity = new DevelopmentIdentity(TimeProvider.System, accounts, options);
        await using var app = LauncherApi.Create(options, accounts, identity, TimeProvider.System, mods: store);
        await app.StartAsync();
        using HttpClient http = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var advertised = (await http.GetFromJsonAsync<ServerReply>("/api/v1/server"))!;
        Assert.Equal(2, advertised.ApiVersion);
        Assert.Null(advertised.SkillPackage);
        Assert.Equal(set.Id, advertised.ModSet!.Id);
        Assert.Equal(new(AutoMiningFilter.ModId, "1.2.0"), Assert.Single(advertised.ModSet.Mods));
        using var denied = await http.GetAsync($"/api/v2/mod-sets/{set.Id}/code");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        byte[] code = await http.GetByteArrayAsync($"/api/v2/mod-sets/{set.Id}/code");
        Assert.Equal(set.CodeSha256, Convert.ToHexString(SHA256.HashData(code)));
        using (ZipArchive downloaded = new(new MemoryStream(code)))
        {
            Assert.NotNull(downloaded.GetEntry("eden_mod_loader.pyj"));
            Assert.Contains(downloaded.Entries, entry => entry.FullName.EndsWith("/automining.pyj", StringComparison.Ordinal));
        }
        byte[] staged = File.ReadAllBytes(Path.Combine(client, "tq", "manifest.dat"));
        Convert.FromHexString(ClientManifest.OriginalCodeHash).CopyTo(staged, ClientManifest.CodeHashOffset);
        string prefix = Convert.ToBase64String(ClientManifest.ReadOriginalPrefix(staged));
        using var stale = await http.PostAsJsonAsync("/api/v1/launch-tickets", new LaunchRequest(3662168, prefix));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var legacy = await http.PostAsJsonAsync("/api/v1/launch-tickets", new LaunchRequest(3662168, prefix, SkillPackageId: set.Id));
        Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        using var launched = await http.PostAsJsonAsync("/api/v1/launch-tickets", new LaunchRequest(3662168, prefix, ModSetId: set.Id));
        Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
        var reply = (await launched.Content.ReadFromJsonAsync<LaunchReply>())!;
        Assert.Equal(
            set.CodeSha256,
            Convert.ToHexString(Convert.FromBase64String(reply.Manifest).AsSpan(ClientManifest.CodeHashOffset, SHA256.HashSizeInBytes))
        );
        accounts.Revoke(session.AccessToken);
        using var revoked = await http.GetAsync($"/api/v2/mod-sets/{set.Id}/code");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        await app.StopAsync();
    }
}
