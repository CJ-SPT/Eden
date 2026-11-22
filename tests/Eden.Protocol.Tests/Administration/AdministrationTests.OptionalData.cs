using System.Net;
using System.Text.RegularExpressions;
using Eden.Protocol.Tests.Capsuleers;
using Eden.Server.Administration;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed partial class AdministrationTests
{
    [Fact]
    public async Task OptionalDataPageExplainsFallbacksAndProtectsReimport()
    {
        const string page = "/optional-data";
        const string traffic = "/api/admin/optional-data/traffic";
        await using var fixture = await Fixture.Start();
        using var anonymousPage = await fixture.Client.GetAsync(page);
        Assert.Equal(HttpStatusCode.Redirect, anonymousPage.StatusCode);
        Assert.StartsWith("/login?", new Uri(fixture.Client.BaseAddress!, anonymousPage.Headers.Location!).PathAndQuery);
        using var anonymousImport = await fixture.Client.PostAsync(traffic, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousImport.StatusCode);

        await fixture.SignIn();
        string html = WebUtility.HtmlDecode(await fixture.Client.GetStringAsync(page));
        Assert.Contains("Standard preset is unavailable", html);
        Assert.Contains("while Standard is selected so Eden can restart", html);
        Assert.Contains("generated Tech I freighters", html);
        Assert.Contains("/api/admin/optional-data/market", html);
        Assert.Contains("Import market orders", html);
        Assert.Contains("Import zKillboard traffic", html);
        Assert.Contains("Restart Eden", html);
        fixture.Client.DefaultRequestHeaders.Remove(AdministrationHost.CsrfHeader);
        using var missingToken = await fixture.Client.PostAsync(traffic, null);
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        var database = fixture.World.Services.GetRequiredService<WorldDatabase>();
        TrafficSnapshotTests.Install(database, TrafficSnapshotTests.Profile());
        html = WebUtility.HtmlDecode(await fixture.Client.GetStringAsync(page));
        Assert.Contains("Re-import zKillboard traffic", html);
        Assert.Contains("1 pilot and 1 fitting", Regex.Replace(html, @"\s+", " "));
        byte[] original = await File.ReadAllBytesAsync(database.TrafficSnapshotPath);
        using FileStream owner = new(
            database.TrafficSnapshotPath + ".import.owner",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );

        string token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        using var reimport = await fixture.Client.PostAsync(traffic, new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.Redirect, reimport.StatusCode);
        Assert.Equal(page, reimport.Headers.Location!.OriginalString);

        var jobs = fixture.App.Services.GetRequiredService<OptionalDataImports>();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (jobs.Status().Traffic?.Running == true)
        {
            await Task.Delay(20, timeout.Token);
        }
        Assert.True(jobs.Status().Traffic?.Failed);
        Assert.False(File.Exists(database.TrafficSnapshotPath + ".import.pending"));
        html = await fixture.Client.GetStringAsync(page, timeout.Token);
        Assert.Contains("The previous snapshot, if present, is still installed.", html);
        Assert.Contains("Re-import zKillboard traffic", html);
        Assert.Equal(original, await File.ReadAllBytesAsync(database.TrafficSnapshotPath, timeout.Token));
    }
}
