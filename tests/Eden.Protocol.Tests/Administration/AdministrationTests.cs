using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eden.Data.Catalog;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Server.Administration;
using Eden.World.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed class AdministrationTests
{
    private const string Password = "local administrator password";

    private sealed class Fixture : IAsyncDisposable
    {
        internal TestWorld World { get; } = new();
        internal AdministratorAccounts Accounts { get; }
        internal WebApplication App { get; }
        internal HttpClient Client { get; }

        private Fixture()
        {
            Accounts = new(World.Path);
            Accounts.SetPassword("Corey", Password);
            App = AdministrationHost.Create(World.Services, World.Path, 0);
            Client = new(new HttpClientHandler { AllowAutoRedirect = false });
        }

        internal static async Task<Fixture> Start()
        {
            Fixture fixture = new();
            await fixture.App.StartAsync();
            fixture.Client.BaseAddress = new Uri(Assert.Single(fixture.App.Urls));
            return fixture;
        }

        internal async Task SignIn()
        {
            string page = await Client.GetStringAsync("/login");
            string token = WebUtility.HtmlDecode(
                Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value
            );
            Assert.NotEmpty(token);
            using var reply = await Client.PostAsync(
                "/session/login",
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["name"] = "Corey",
                        ["password"] = Password,
                        ["__RequestVerificationToken"] = token,
                    }
                )
            );
            Assert.Equal(HttpStatusCode.Redirect, reply.StatusCode);
            Assert.Equal("/", reply.Headers.Location!.OriginalString);
            var csrf = await Client.GetFromJsonAsync<JsonElement>("/api/admin/antiforgery");
            Client.DefaultRequestHeaders.Add(AdministrationHost.CsrfHeader, csrf.GetProperty("token").GetString());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            World.Dispose();
        }
    }

    [Fact]
    public async Task AuthenticationProtectsEveryOperationAndRequiresLocalHostAndAntiforgery()
    {
        await using var fixture = await Fixture.Start();
        using var unauthenticated = await fixture.Client.GetAsync("/api/admin/configuration");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var noToken = await fixture.Client.PostAsync(
            "/session/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Corey", ["password"] = Password })
        );
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        using var externalHost = new HttpRequestMessage(HttpMethod.Get, "/login");
        externalHost.Headers.Host = "outside.example";
        using var denied = await fixture.Client.SendAsync(externalHost);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await fixture.SignIn();
        using var script = await fixture.Client.GetAsync("/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("javascript", script.Content.Headers.ContentType?.MediaType ?? "missing content type");
        Assert.True((await script.Content.ReadAsByteArrayAsync()).Length > 1000);
        string html = await fixture.Client.GetStringAsync("/");
        Assert.Contains("World settings", html);
        Assert.Contains("Original, active and pending values", html);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/admin/configuration");
        crossOrigin.Headers.Add("Origin", "https://outside.example");
        using var blocked = await fixture.Client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        fixture.Client.DefaultRequestHeaders.Remove(AdministrationHost.CsrfHeader);
        var state = await fixture.Client.GetFromJsonAsync<ConfigurationState>("/api/admin/configuration");
        using var noSaveToken = await fixture.Client.PostAsJsonAsync(
            "/api/admin/revisions",
            new AdminSaveRequest(state!.Generation, state.Active.Draft)
        );
        Assert.Equal(HttpStatusCode.BadRequest, noSaveToken.StatusCode);
    }

    [Fact]
    public async Task PopulationRequiresAdministratorAndReportsSavedCoverageWithoutAdvancingJobs()
    {
        await using var fixture = await Fixture.Start();
        using var denied = await fixture.Client.GetAsync("/api/admin/population");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var pageDenied = await fixture.Client.GetAsync("/population");
        Assert.Equal(HttpStatusCode.Redirect, pageDenied.StatusCode);
        await fixture.SignIn();
        var population = fixture.World.Services.GetRequiredService<ICapsuleerPopulation>();
        var scheduler = fixture.World.Services.GetRequiredService<ICapsuleerScheduler>();
        var before = scheduler.Jobs().ToArray();
        var snapshot = (await fixture.Client.GetFromJsonAsync<AdminPopulationSnapshot>("/api/admin/population"))!;
        Assert.Equal(population.Summary(), snapshot.Population);
        Assert.Equal(population.Systems().Count, snapshot.Systems.Count);
        Assert.Equal(before, scheduler.Jobs());
        string html = await fixture.Client.GetStringAsync("/population");
        Assert.Contains("Autonomous population", html);
        Assert.Contains("href=\"/population\"", html);
    }

    [Fact]
    public async Task PerformanceRequiresAdministratorAndServesLiveProcessDiagnosticsInDarkEditor()
    {
        await using var fixture = await Fixture.Start();
        using var denied = await fixture.Client.GetAsync("/api/admin/performance");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var pageDenied = await fixture.Client.GetAsync("/performance");
        Assert.Equal(HttpStatusCode.Redirect, pageDenied.StatusCode);
        await fixture.SignIn();
        using var response = await fixture.Client.GetAsync("/api/admin/performance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var snapshot = (await response.Content.ReadFromJsonAsync<ServerPerformanceSnapshot>())!;
        Assert.True(snapshot.Resources.WorkingSetBytes > 0);
        Assert.True(snapshot.Resources.ProcessorCount > 0);
        Assert.True(snapshot.Resources.Uptime > TimeSpan.Zero);
        Assert.True(snapshot.Resources.ProcessThreads > 0);
        Assert.Equal(0, snapshot.ActiveSessions);
        Assert.False(snapshot.ResidentWorld);
        Assert.Null(snapshot.LastCheckpoint);
        string html = await fixture.Client.GetStringAsync("/performance");
        Assert.Contains("Server performance", html);
        Assert.Contains("Memory in RAM", html);
        Assert.Contains("color-scheme: dark", html);
        Assert.Contains("--background: #0d141e", html);
        Assert.Contains("href=\"/performance\"", html);
        foreach (string page in new[] { "/", "/catalog", "/revisions", "/login" })
        {
            Assert.Contains("color-scheme: dark", await fixture.Client.GetStringAsync(page));
        }
    }

    [Fact]
    public async Task BrowsingValidationSavingComparisonAndRestoreRetainPendingRestart()
    {
        await using var fixture = await Fixture.Start();
        await fixture.SignIn();
        var datasets = await fixture.Client.GetFromJsonAsync<CatalogDatasetDescriptor[]>("/api/admin/datasets");
        Assert.Equal(442, datasets!.Length);
        var page = await fixture.Client.GetFromJsonAsync<CatalogBrowsePage>("/api/admin/catalog?dataset=client/types&limit=1");
        Assert.Single(page!.Rows);
        Assert.NotNull(page.NextCursor);
        using var invalidPage = await fixture.Client.GetAsync("/api/admin/catalog?dataset=client/types&limit=501");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        var state = await fixture.Client.GetFromJsonAsync<ConfigurationState>("/api/admin/configuration");
        var draft = state!.Active.Draft with { Settings = state.Active.Draft.Settings with { Resources = new(false, 7) } };
        string unknownField = JsonSerializer
            .Serialize(new AdminSaveRequest(state.Generation, draft), JsonSerializerOptions.Web)
            .Insert(1, "\"volume\":12,");
        using var nativeField = await fixture.Client.PostAsync(
            "/api/admin/revisions",
            new StringContent(unknownField, Encoding.UTF8, "application/json")
        );
        Assert.Equal(HttpStatusCode.BadRequest, nativeField.StatusCode);
        using var invalid = await fixture.Client.PostAsJsonAsync(
            "/api/admin/validate",
            draft with
            {
                Settings = draft.Settings with { Resources = new(false, 99) },
            }
        );
        Assert.False((await invalid.Content.ReadFromJsonAsync<AdminValidationResult>())!.Valid);
        using var saved = await fixture.Client.PostAsJsonAsync("/api/admin/revisions", new AdminSaveRequest(state.Generation, draft));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var pending = (await saved.Content.ReadFromJsonAsync<ConfigurationState>())!;
        Assert.Equal(state.Active.Id, pending.Active.Id);
        Assert.NotNull(pending.Pending);
        string html = await fixture.Client.GetStringAsync("/");
        Assert.Contains("pending restart", html);
        Assert.Contains("Revision 2", html);
        var differences = await fixture.Client.GetFromJsonAsync<RevisionDifference[]>("/api/admin/compare?left=1&right=2");
        Assert.Contains(differences!, difference => difference.Field == "Belt renewal");
        using var stale = await fixture.Client.PostAsJsonAsync("/api/admin/revisions", new AdminSaveRequest(state.Generation, draft));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var restored = await fixture.Client.PostAsJsonAsync(
            "/api/admin/restore",
            new AdminRestoreRequest(pending.Generation, state.Active.Id)
        );
        var restore = (await restored.Content.ReadFromJsonAsync<ConfigurationState>())!;
        Assert.Equal(3, restore.Pending!.Id);
        Assert.Equal(1, restore.Active.Id);
        using var export = await fixture.Client.GetAsync("/api/admin/export/1");
        Assert.Equal("attachment", export.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(1, (await export.Content.ReadFromJsonAsync<WorldConfigurationDraft>())!.FormatVersion);
    }

    [Fact]
    public async Task PasswordRotationRevokesHttpAndCircuitOperationsAndGameAccountsHaveNoAccess()
    {
        await using var fixture = await Fixture.Start();
        await fixture.SignIn();
        var principal = fixture.Accounts.Authenticate("Corey", Password)!;
        using var scope = fixture.App.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<AdministrationOperations>();
        var gameAccount = new ClaimsPrincipal(
            new ClaimsIdentity([new(ClaimTypes.Name, "Corey"), new(ClaimTypes.Role, "Administrator")], "game")
        );
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.Read(gameAccount));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.Performance(gameAccount));
        fixture.Accounts.SetPassword("Corey", "replacement administrator password");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.Read(principal));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.Performance(principal));
        using var revoked = await fixture.Client.GetAsync("/api/admin/configuration");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var performanceRevoked = await fixture.Client.GetAsync("/api/admin/performance");
        Assert.Equal(HttpStatusCode.Unauthorized, performanceRevoked.StatusCode);
        Assert.Null(fixture.Accounts.Authenticate("Corey", Password));
    }
}
