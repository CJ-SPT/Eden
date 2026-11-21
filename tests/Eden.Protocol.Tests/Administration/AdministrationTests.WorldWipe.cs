using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Eden.Gameplay.Configuration;
using Eden.Server.Administration;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed partial class AdministrationTests
{
    [Fact]
    public async Task WorldWipeRequiresCurrentAdministratorAntiforgeryAndExactConfirmation()
    {
        await using var fixture = await Fixture.Start();
        var wipe = fixture.App.Services.GetRequiredService<WorldWipeRequest>();
        long generation = fixture.World.Services.GetRequiredService<IWorldConfiguration>().Read().Generation;
        using var anonymous = await fixture.Client.PostAsync("/api/admin/world/wipe", Form(WorldWipeRequest.Confirmation, generation));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(wipe.Pending);
        await fixture.SignIn();
        string page = await fixture.Client.GetStringAsync("/world");
        Assert.Contains("Back up, wipe world and stop server", page);
        Assert.Contains("Accounts, passwords", page);
        Assert.Contains("Recreate with existing settings", page);
        Assert.Contains("Configure new settings", page);
        using var stale = await fixture.Client.PostAsync("/api/admin/world/wipe", Form(WorldWipeRequest.Confirmation, generation - 1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Null(wipe.Pending);
        using var incorrect = await fixture.Client.PostAsync("/api/admin/world/wipe", Form("wipe world", generation));
        Assert.Equal(HttpStatusCode.BadRequest, incorrect.StatusCode);
        Assert.Null(wipe.Pending);
        string csrf = fixture.Client.DefaultRequestHeaders.GetValues(AdministrationHost.CsrfHeader).Single();
        fixture.Client.DefaultRequestHeaders.Remove(AdministrationHost.CsrfHeader);
        using var noCsrf = await fixture.Client.PostAsync("/api/admin/world/wipe", Form(WorldWipeRequest.Confirmation, generation));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Null(wipe.Pending);
        string formToken = WebUtility.HtmlDecode(
            Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value
        );
        Assert.NotEmpty(formToken);
        using var accepted = await fixture.Client.PostAsync(
            "/api/admin/world/wipe",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["confirmation"] = WorldWipeRequest.Confirmation,
                    ["__RequestVerificationToken"] = formToken,
                    ["recreation"] = "configure",
                    ["generation"] = generation.ToString(CultureInfo.InvariantCulture),
                }
            )
        );
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(WorldRecreation.Configure, wipe.Pending!.Recreation);
        Assert.Contains(WebUtility.HtmlEncode(wipe.Pending!.BackupDirectory), await accepted.Content.ReadAsStringAsync());
        await wipe.ShutdownRequested.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(fixture.World.Store.ListPilots(1000001));
        fixture.Client.DefaultRequestHeaders.Add(AdministrationHost.CsrfHeader, csrf);
        using var duplicate = await fixture.Client.PostAsync("/api/admin/world/wipe", Form(WorldWipeRequest.Confirmation, generation));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    private static FormUrlEncodedContent Form(string confirmation, long generation) =>
        new(
            new Dictionary<string, string>
            {
                ["confirmation"] = confirmation,
                ["recreation"] = "reuse",
                ["generation"] = generation.ToString(CultureInfo.InvariantCulture),
            }
        );
}
