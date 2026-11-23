using System.Net;
using System.Text.RegularExpressions;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Capsuleers;
using Eden.World.Characters;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed partial class AdministrationTests
{
    [Fact]
    public async Task KillboardFiltersCommittedKillsAndLossesAndPagesAcrossRestart()
    {
        const int system = 30000142;
        const long playerId = 90000001;
        const long botId = 90000002;
        const int botWaves = 51;
        long time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc();
        CombatParty player = new(100, playerId, 1000044, null, 601, null, null, 10);
        CombatParty bot = new(101, botId, 1000044, null, 601, null, null, 10);
        CombatParty npc = new(102, null, 1000127, null, 30380, null, null, 10);
        await using var fixture = await Fixture.Start(liveWorld: true);
        var live = fixture.World.Services.GetRequiredService<LiveWorld>();
        var board = fixture.World.Services.GetRequiredService<IServerKillboard>();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            if (action.Set<Account>().Find(CapsuleerCommunityStore.MinerAccountId) is null)
            {
                action.Add(
                    new Account
                    {
                        Id = CapsuleerCommunityStore.MinerAccountId,
                        UserName = "Autonomous capsuleers",
                        CloneGrade = AccountCloneGrade.Omega,
                    }
                );
            }
            action.Pilots.Find(playerId)!.Name = "Killboard Player";
            var pilot = action.Pilots.Find(botId)!;
            pilot.Name = "Killboard Bot";
            pilot.AccountId = CapsuleerCommunityStore.MinerAccountId;
            action.Commit();
        }
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            PilotInteractionHistoryStore.Destroyed(action, playerId, "ship:100", time + 1, system, player, bot, [], true);
            PilotInteractionHistoryStore.Destroyed(action, botId, "ship:101", time + 1, system, bot, npc, [], true);
            for (int wave = 0; wave < botWaves; wave++)
            {
                var target = npc with { EntityId = npc.EntityId + wave };
                PilotInteractionHistoryStore.Destroyed(action, botId, $"npc:{target.EntityId}:{time}", time, system, target, bot, []);
            }
            PilotInteractionHistoryStore.Destroyed(action, playerId, "npc:200", time - 1, system, npc with { EntityId = 200 }, player, []);
            action.Commit();
        }
        using (var rollback = live.Economy.Begin(TransactionParticipants.None))
        {
            PilotInteractionHistoryStore.Destroyed(rollback, botId, "npc:201", time + 2, system, npc with { EntityId = 201 }, bot, []);
        }
        using var denied = await fixture.Client.GetAsync("/killboard");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        await fixture.SignIn();
        Assert.Contains("href=\"/killboard\"", await fixture.Client.GetStringAsync("/"));
        string losses = await fixture.Client.GetStringAsync("/killboard?killer=Bot&victim=Player");
        Assert.Single(ReportIds(losses));
        Assert.Contains("Killboard Player", losses);
        Assert.Contains("Killboard Bot", losses);
        Assert.Contains("2026-01-02 03:04:05", losses);
        Assert.Contains("Ibis", losses);
        Assert.Contains("Jita", losses);
        Assert.Single(ReportIds(await fixture.Client.GetStringAsync("/killboard?killer=Npc&victim=Bot")));
        Assert.Single(ReportIds(await fixture.Client.GetStringAsync("/killboard?killer=Player&victim=Npc")));
        Assert.Contains(
            "No combat reports match these filters.",
            await fixture.Client.GetStringAsync("/killboard?killer=Player&victim=Bot")
        );
        string first = await fixture.Client.GetStringAsync("/killboard?killer=Bot&victim=Npc");
        Assert.Equal(50, ReportIds(first).Length);
        string older = WebUtility.HtmlDecode(Regex.Match(first, "href=\"([^\"]+)\">Older reports").Groups[1].Value);
        Assert.Contains("killer=Bot", older);
        Assert.Contains("victim=Npc", older);
        string second = await fixture.Client.GetStringAsync(older);
        Assert.DoesNotContain(Assert.Single(ReportIds(second)), ReportIds(first));
        Assert.Contains("Latest reports", second);
        Assert.DoesNotContain("Older reports", second);

        KillboardFilter all = new(null, null);
        KillboardPage firstPage;
        using (GameplaySqlGuard.Begin())
        using (var metrics = SessionPerformance.Begin("killboard", _ => { }, force: true)!)
        {
            firstPage = board.Read(all)!;
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        var lastPage = board.Read(all with { Before = firstPage.Older })!;
        Assert.Equal(botWaves + 3, firstPage.Entries.Count + lastPage.Entries.Count);
        Assert.Equal(time - 1, lastPage.Entries[^1].Cursor.Time);
        await live.FlushAsync();
        live.Dispose();
        using LiveWorld restarted = new(new(fixture.World.Path), new TestClock(), _ => { });
        Assert.Equivalent(firstPage, board.Read(all), strict: true);
        Assert.Equivalent(lastPage, board.Read(all with { Before = firstPage.Older }), strict: true);
    }

    private static string[] ReportIds(string html) =>
        Regex.Matches(html, "data-report-id=\"(\\d+)\"").Select(match => match.Groups[1].Value).ToArray();
}
