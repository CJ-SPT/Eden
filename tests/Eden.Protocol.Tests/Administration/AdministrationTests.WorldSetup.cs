using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Eden.Gameplay.Configuration;
using Eden.Server.Administration;
using Eden.Server.Configuration;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Administration;

public sealed partial class AdministrationTests
{
    [Theory]
    [InlineData("established", false)]
    [InlineData("fresh", true)]
    public async Task WorldSetupRequiresAdminAndPersistsProfileBeforeInitialization(string profile, bool fresh)
    {
        using TestWorld catalog = new();
        string path = Path.Combine(Path.GetDirectoryName(catalog.Path)!, "first-start.sqlite");
        WorldDatabase database = new(path);
        var administrators = new AdministratorAccounts(path);
        administrators.SetPassword("Corey", Password);
        var state = WorldSetupStore.Read(path)!;
        Assert.True(state.NewWorld);
        Assert.Null(state.Approval);
        WorldSetupRequest setup = new(database, state);
        await using var app = AdministrationHost.Create(null, path, 0, worldSetup: setup);
        await app.StartAsync();
        using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(Assert.Single(app.Urls)),
        };
        using var root = await client.GetAsync("/");
        Assert.Equal("/world/setup", root.Headers.Location?.OriginalString);
        using var unauthorized = await client.PostAsync("/api/admin/world/setup", SetupForm(profile));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.False(File.Exists(path));

        string login = await client.GetStringAsync("/login");
        using var signIn = await client.PostAsync(
            "/session/login",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["name"] = "Corey",
                    ["password"] = Password,
                    ["__RequestVerificationToken"] = AntiforgeryTokenFrom(login),
                }
            )
        );
        Assert.Equal("/world/setup", signIn.Headers.Location?.OriginalString);
        string page = await client.GetStringAsync("/world/setup");
        Assert.Contains("Established Universe", page);
        Assert.Contains("Fresh Universe", page);
        var draft = await client.GetFromJsonAsync<WorldConfigurationDraft>($"/api/admin/world/setup-draft?profile={profile}");
        Assert.Equal(fresh, draft!.Settings.FreshUniverse);
        var custom = draft with { Settings = draft.Settings with { Capsuleers = draft.Settings.Capsuleers with { ActivePilotCap = 5 } } };
        using var noToken = await client.PostAsync("/api/admin/world/setup", SetupForm(profile));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        string token = AntiforgeryTokenFrom(page);
        using var invalid = await client.PostAsync("/api/admin/world/setup", SetupForm("none", token));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.False(File.Exists(path));

        using var accepted = await client.PostAsync("/api/admin/world/setup", SetupForm(profile, token, WorldSetupStore.Export(custom)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await setup.Approved.WaitAsync(TimeSpan.FromSeconds(10));
        using var duplicate = await client.PostAsync("/api/admin/world/setup", SetupForm(profile, token));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await app.StopAsync();
        var approval = WorldSetupStore.Read(path)!.Approval!;
        Assert.Equal("Corey", approval.Author);
        Assert.Equal(5, approval.Draft.Settings.Capsuleers.ActivePilotCap);

        Assert.Throws<InvalidOperationException>(() => ConfigurationCommands.TryRun(["--configuration-history"], path));

        // Restart after schema creation but before the approved revision becomes active.
        using (WorldDbContext world = new(path))
        using (CatalogDbContext definitions = new(database.CatalogPath))
        {
            WorldMigration.Open(world, definitions, TimeProvider.System, setup: approval);
        }
        WorldInitialization.Initialize(database, setup: WorldSetupStore.Read(path)!.Approval);
        Assert.Null(WorldSetupStore.Read(path));
        var active = new WorldConfigurationStore(database, TimeProvider.System).Read().Active;
        Assert.Equal("Corey", active.Author);
        Assert.Equal(5, active.Draft.Settings.Capsuleers.ActivePilotCap);
        Assert.Equal(fresh, database.Settings.FreshUniverse);
        Assert.Equal(fresh, database.Settings.UsesFreshUniverseMarket());
        Assert.Equal(!fresh, database.Settings.DevelopmentSupply.TestingGrants);
        Assert.Equal(!fresh, database.Settings.Economy.RegionalHubSupply);
        Assert.Equal(!fresh, WorldInitialization.Open(database).ListPilots(1000001).Count > 0);
    }

    private static FormUrlEncodedContent SetupForm(string profile, string token = "", string document = "") =>
        new(
            new Dictionary<string, string>
            {
                ["profile"] = profile,
                ["document"] = document,
                ["__RequestVerificationToken"] = token,
            }
        );

    private static string AntiforgeryTokenFrom(string page)
    {
        string token = WebUtility.HtmlDecode(
            Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value
        );
        Assert.NotEmpty(token);
        return token;
    }
}
