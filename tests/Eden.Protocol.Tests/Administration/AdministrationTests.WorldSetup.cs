using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;
using Eden.Protocol.Tests.Market;
using Eden.Server;
using Eden.Server.Administration;
using Eden.Server.Configuration;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed partial class AdministrationTests
{
    [Fact]
    public async Task WorldSetupRequiresAdminAndPersistsEstablishedEconomyBeforeInitialization()
    {
        using TestWorld catalog = new();
        string path = Path.Combine(Path.GetDirectoryName(catalog.Path)!, "first-start.sqlite");
        WorldDatabase database = new(path);
        PassiveCatalogFixture.LoadUtilityHulls(database.CatalogPath, new HashSet<int> { 34 });
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
        using var unauthorized = await client.PostAsync("/api/admin/world/setup", SetupForm());
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
        Assert.Contains("data-tools", page);
        Assert.Contains("Import-LiveMarket.py", page);
        Assert.DoesNotContain("Fresh Universe", page);
        Assert.DoesNotContain("name=\"profile\"", page);
        using var retiredDraft = await client.GetAsync("/api/admin/world/setup-draft?profile=fresh");
        Assert.Equal(HttpStatusCode.BadRequest, retiredDraft.StatusCode);
        var draft = await client.GetFromJsonAsync<WorldConfigurationDraft>("/api/admin/world/setup-draft");
        Assert.False(draft!.Settings.FreshUniverse);
        Assert.True(draft.Settings.Capsuleers.EarnedPvpShips);
        Assert.Equal(2m, Assert.Single(draft.AdjustedPrices, price => price.TypeId == 34).PriceIsk);
        var custom = draft with { Settings = draft.Settings with { Capsuleers = draft.Settings.Capsuleers with { ActivePilotCap = 5 } } };
        using var noToken = await client.PostAsync("/api/admin/world/setup", SetupForm());
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        string token = AntiforgeryTokenFrom(page);
        using var retiredProfile = await client.PostAsync("/api/admin/world/setup", SetupForm(token, profile: "fresh"));
        Assert.Equal(HttpStatusCode.BadRequest, retiredProfile.StatusCode);
        var fresh = draft with { Settings = draft.Settings with { FreshUniverse = true } };
        using var retiredSettings = await client.PostAsync("/api/admin/world/setup", SetupForm(token, WorldSetupStore.Export(fresh)));
        Assert.Equal(HttpStatusCode.BadRequest, retiredSettings.StatusCode);
        Assert.False(File.Exists(path));

        using var missingSnapshot = await client.PostAsync("/api/admin/world/setup", SetupForm(token));
        Assert.Equal(HttpStatusCode.BadRequest, missingSnapshot.StatusCode);
        Assert.Contains("Import a compatible live market order snapshot", await missingSnapshot.Content.ReadAsStringAsync());
        Assert.False(File.Exists(path));
        StandardMarketTests.InstallSetupSnapshot(database);

        using var accepted = await client.PostAsync("/api/admin/world/setup", SetupForm(token, WorldSetupStore.Export(custom)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await setup.Approved.WaitAsync(TimeSpan.FromSeconds(10));
        using var duplicate = await client.PostAsync("/api/admin/world/setup", SetupForm(token));
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
        Assert.False(database.Settings.FreshUniverse);
        Assert.True(database.Settings.Capsuleers.EarnedPvpShips);
        Assert.True(database.Settings.DevelopmentSupply.TestingGrants);
        Assert.False(database.Settings.DevelopmentSupply.TestingMarket);
        Assert.True(
            database.Settings.Economy
                is {
                    ChargeServiceCosts: true,
                    StandardMarket: true,
                    ProgressiveMarkets: false,
                    RegionalHubSupply: false,
                    BeginnerMarketSupply: false,
                }
        );
        Assert.Equal(2m, Assert.Single(active.Draft.AdjustedPrices, price => price.TypeId == 34).PriceIsk);
        Assert.NotEmpty(WorldInitialization.Open(database).ListPilots(1000001));
        using var host = ServerServices.Build(database, liveWorld: true);
        var market = host.GetRequiredService<IPilotMarket>();
        var order = Assert.Single(market.Orders(90000001, 34));
        Assert.False(order.Bid);
        Assert.Equal(200, order.PriceCents);
        Assert.Equal(5, order.Remaining);
    }

    private static FormUrlEncodedContent SetupForm(string token = "", string document = "", string profile = "") =>
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
