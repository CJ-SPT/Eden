using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Administration;

public sealed partial class AdministrationTests
{
    [Fact]
    public async Task PopulationInspectorLinksResidentBotTrainingCorporationAndAllianceWork()
    {
        const int corporation = CorporationRules.FirstCorporationId;
        const int alliance = AllianceRules.FirstAllianceId;
        const int home = 30000141;
        const int target = 30000142;
        const int skill = (int)IndustrySkill.Industry;
        const long bot = 90000002;
        var now = DateTimeOffset.UtcNow;
        await using var fixture = await Fixture.Start(liveWorld: true);
        var services = fixture.World.Services;
        var live = services.GetRequiredService<LiveWorld>();
        HaulerProgress work;
        using (var world = live.Economy.Begin(TransactionParticipants.None))
        {
            var pilot = world.Pilots.Find(bot)!;
            int station = pilot.ActiveShip.StationId;
            if (world.Set<Account>().Find(CapsuleerCommunityStore.MinerAccountId) is null)
            {
                world.Add(
                    new Account
                    {
                        Id = CapsuleerCommunityStore.MinerAccountId,
                        UserName = "Autonomous capsuleers",
                        CloneGrade = AccountCloneGrade.Omega,
                    }
                );
            }
            pilot.AccountId = CapsuleerCommunityStore.MinerAccountId;
            world.Add(new SavedCapsuleerPopulation { SystemId = home, HomeStationId = station });
            CapsuleerPopulationStore.AddAgent(world, new(bot, home, CapsuleerCareer.Hauling, pilot.ActiveShipId, 0, 0));
            world.Add(
                new SavedCapsuleerHauler
                {
                    Id = home,
                    PilotId = bot,
                    SourceStationId = station,
                    DestinationStationId = station,
                    FounderShipId = pilot.ActiveShipId,
                }
            );
            work = new(
                HaulerActivity.Waiting,
                0,
                0,
                pilot.ActiveShipId,
                station,
                0,
                new NavigationState().Capture(),
                "Waiting for a freight order"
            );
            new HaulerCheckpoint(bot, work).Apply(world);
            CapsuleerJobCheckpoint.Apply(world, new(bot, 0, 0, TimeSpan.TicksPerMinute, CapsuleerAction.Retry));
            var details = PlayerCorporationTests.Details("Inspector Works", "INSP");
            world.Add(
                new SavedCorporation
                {
                    Id = corporation,
                    Name = details.Name,
                    Ticker = details.Ticker,
                    CeoId = bot,
                    CreatorId = bot,
                    MemberLimit = 20,
                    StationId = station,
                    SystemId = home,
                    CapsuleerFoundingSystemId = home,
                    BalanceCents = 123456,
                    DetailsJson = JsonSerializer.Serialize(details),
                }
            );
            world.Add(new InventoryOwner { Id = corporation });
            pilot.CorporationId = corporation;
            world.Add(
                new SavedAlliance
                {
                    Id = alliance,
                    Name = "Inspection Coalition",
                    Ticker = "INSC",
                    CreatorId = bot,
                    CreatorCorporationId = corporation,
                    ExecutorCorporationId = corporation,
                }
            );
            world.Add(new SavedAllianceMember { CorporationId = corporation, AllianceId = alliance });
            world.Add(
                new SavedAllianceBill
                {
                    AllianceId = alliance,
                    IssuedAt = now.ToFileTime(),
                    DueAt = now.AddDays(1).ToFileTime(),
                    AmountCents = 250000,
                    CorporationCount = 1,
                }
            );
            _ = world.Set<PilotSkill>().Find(bot, skill) ?? world.Add(new PilotSkill { PilotId = bot, TypeId = skill }).Entity;
            world.Add(
                new QueuedLevel
                {
                    PilotId = bot,
                    Position = 0,
                    TypeId = skill,
                    Level = 1,
                    EndPoints = 250,
                    StartTime = now.AddMinutes(-1).ToFileTime(),
                    EndTime = now.AddMinutes(20).ToFileTime(),
                }
            );
            world.Commit();
        }
        var inspection = services.GetRequiredService<ICapsuleerInspection>();
        var dormant = Assert.IsType<CapsuleerPilotInspection>(inspection.Pilot(bot));
        Assert.Equal("Waiting for a freight order", dormant.WaitingReason);
        Assert.Equal(CapsuleerAction.Retry, dormant.NextAction);
        using (live.EnterShared("publish inspector test checkpoint"))
        {
            live.HaulerCheckpoints[bot] = new(
                bot,
                work with
                {
                    Activity = HaulerActivity.Flying,
                    WaitingReason = null,
                    CareerPlan = new(CapsuleerProfession.Hauling, HaulingRules.BadgerType, CapsuleerTrainingFocus.Ship),
                    SovereigntyExpedition = new(alliance, corporation, target, 0, 0, Duty: SovereigntyDuty.Defend),
                }
            );
            live.CapsuleerJobs[bot] = new(bot, 1, 1, TimeSpan.TicksPerMinute, CapsuleerAction.Arrival);
        }

        string botApi = $"/api/admin/population/bots/{bot}";
        using var denied = await fixture.Client.GetAsync(botApi);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await fixture.SignIn();

        var catalog = services.GetRequiredService<CatalogProvider>();
        _ = catalog.Stations;
        _ = catalog.NpcCorporations;
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            Assert.Equal(bot, Assert.Single(inspection.Browse(CapsuleerDirectory.Bots, bot.ToString()).Entries).Id);
            Assert.Equal(corporation, Assert.Single(inspection.Browse(CapsuleerDirectory.Corporations, " insp ").Entries).Id);
            Assert.Equal(alliance, Assert.Single(inspection.Browse(CapsuleerDirectory.Alliances, "INSC", int.MaxValue).Entries).Id);
            var pilot = Assert.IsType<CapsuleerPilotInspection>(inspection.Pilot(bot));
            Assert.Equal("Defend sovereignty", pilot.Work.Goal);
            Assert.Equal(target, pilot.Work.SystemId);
            Assert.Equal(HaulingRules.BadgerType, pilot.CareerPlan!.ShipTypeId);
            Assert.Equal(skill, Assert.Single(pilot.Skills).TypeId);
            Assert.Equal(CapsuleerAction.Arrival, pilot.NextAction);
            Assert.False(pilot.SimulationActive);
            Assert.Equal(new CapsuleerJob(bot, 1, 1, TimeSpan.TicksPerMinute, CapsuleerAction.Arrival), live.CapsuleerJobs[bot]);
            var company = Assert.IsType<CapsuleerCorporationInspection>(inspection.Corporation(corporation));
            Assert.Equal(bot, Assert.Single(company.Members).Id);
            Assert.Equal(123456, company.BalanceCents);
            Assert.Equal(alliance, company.Alliance!.Id);
            var coalition = Assert.IsType<CapsuleerAllianceInspection>(inspection.Alliance(alliance));
            Assert.Equal(corporation, Assert.Single(coalition.Corporations).Id);
            Assert.Equal(SovereigntyDuty.Defend, Assert.Single(coalition.Expeditions).Duty);
            Assert.Equal(250000, coalition.BillCents);
            Assert.Null(inspection.Pilot(90000001));
        });

        using var botResponse = await fixture.Client.GetAsync(botApi);
        Assert.True(botResponse.Headers.CacheControl!.NoStore);
        Assert.Equal(bot, (await botResponse.Content.ReadFromJsonAsync<CapsuleerPilotInspection>())!.Id);
        var directory = await fixture.Client.GetFromJsonAsync<CapsuleerDirectoryPage>(
            "/api/admin/population/directory?directory=Corporations&search=INSP"
        );
        Assert.Equal(corporation, Assert.Single(directory!.Entries).Id);
        var corporationApi = await fixture.Client.GetFromJsonAsync<CapsuleerCorporationInspection>(
            $"/api/admin/population/corporations/{corporation}"
        );
        Assert.Equal(alliance, corporationApi!.Alliance!.Id);
        var allianceApi = await fixture.Client.GetFromJsonAsync<CapsuleerAllianceInspection>($"/api/admin/population/alliances/{alliance}");
        Assert.Equal(bot, Assert.Single(allianceApi!.Expeditions).PilotId);
        using var missing = await fixture.Client.GetAsync("/api/admin/population/bots/0");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        string botPage = await fixture.Client.GetStringAsync($"/population?view=bots&id={bot}");
        Assert.Contains("Current activity", botPage);
        Assert.Contains("Defend sovereignty", botPage);
        Assert.Contains("Next training focus", botPage);
        Assert.Contains($"view=corporations&amp;id={corporation}", botPage);
        string corporationPage = await fixture.Client.GetStringAsync($"/population?view=corporations&id={corporation}");
        Assert.Contains("Inspector Works", corporationPage);
        Assert.Contains($"view=bots&amp;id={bot}", corporationPage);
        Assert.Contains($"view=alliances&amp;id={alliance}", corporationPage);
        string alliancePage = await fixture.Client.GetStringAsync($"/population?view=alliances&id={alliance}");
        Assert.Contains("Inspection Coalition", alliancePage);
        Assert.Contains("Sovereignty assignments", alliancePage);
        Assert.Contains($"view=corporations&amp;id={corporation}", alliancePage);
    }
}
