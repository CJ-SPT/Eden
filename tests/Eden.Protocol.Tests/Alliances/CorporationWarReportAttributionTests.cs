using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Ships;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Alliances;

public sealed class CorporationWarReportAttributionTests
{
    [Fact]
    public async Task WarReportSideFiltersMatchTheClientBeforeAndAfterCheckpoint()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var reports = fixture.Services.GetRequiredService<ICorporationWarReports>();
        const long warId = 1;
        const int attacker = 99000351;
        const int attackingCorp = 98000351;
        const int defender = 98000352;
        const int ally = 98000353;
        const int neutral = 98000354;
        CombatParty aggressor = new(111, 511, attackingCorp, null, 601, null, null, 15, attacker);
        CombatParty defenderPilot = new(112, 512, defender, null, 601, null, null, 25);
        CombatParty allyPilot = new(113, 513, ally, null, 601, null, null, 25);
        CombatParty neutralPilot = new(114, 514, neutral, null, 601, null, null, 25);
        ShipCombatReport aggressorKill = new(1, 512, clock.Now, 30000142, defenderPilot, aggressor, [], []);
        ShipCombatReport allyKill = new(2, 511, clock.Now, 30000142, aggressor, allyPilot, [], []);
        var thirdPartyKill = aggressorKill with { Id = 3, FinalBlow = neutralPilot, OtherAttackers = [aggressor] };
        var friendlyKill = allyKill with
        {
            Id = 4,
            FinalBlow = aggressor with { EntityId = 115, CharacterId = 515 },
            OtherAttackers = [allyPilot],
        };
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(
                new SavedCorporationWar
                {
                    Id = warId,
                    AttackerId = attacker,
                    DefenderId = defender,
                    DeclaredAt = clock.Now - TimeSpan.TicksPerDay,
                    StartsAt = clock.Now,
                }
            );
            action.Add(
                new SavedCorporationWarAlly
                {
                    WarId = warId,
                    OwnerId = ally,
                    AttackerId = attacker,
                    StartsAt = clock.Now,
                    EndsAt = clock.Now + TimeSpan.TicksPerDay,
                }
            );
            CorporationWarReportStore.Save(action, catalog, aggressorKill, new(warId, defender, false));
            CorporationWarReportStore.Save(action, catalog, allyKill, new(warId, attacker, true));
            CorporationWarReportStore.Save(action, catalog, thirdPartyKill, new(warId, defender, false));
            action.Add(
                new SavedCorporationWarKill
                {
                    Id = friendlyKill.Id,
                    WarId = warId,
                    VictimOrganizationId = attacker,
                    AttackerLoss = true,
                    GroupId = (int)HullClass.Corvette,
                    Group = WarReportGroup.Frigates,
                    Time = friendlyKill.Time,
                    IskLost = 100m,
                    Report = JsonSerializer.Serialize(friendlyKill),
                }
            );
            action.Commit();
        }

        AssertReport();
        await live.FlushAsync();
        live.Dispose();
        using LiveWorld reopened = new(database, clock, _ => { });
        AssertReport();

        void AssertReport()
        {
            var totals = reports.Read(warId);
            Assert.Equal(1, totals.ShipsKilled[attacker]);
            Assert.Equal(1, totals.ShipsKilled[defender]);
            var group = totals.KillsByGroup[(int)HullClass.Corvette];
            Assert.Equal(1, group.AttackerShipLoss);
            Assert.Equal(1, group.DefenderShipLoss);
            Assert.Equal([allyKill.Id, aggressorKill.Id], reports.Kills(warId, null, null).Select(kill => kill.Report.Id));
            var attackerEntry = Assert.Single(reports.Kills(warId, attacker, WarReportGroup.Frigates));
            var defenderEntry = Assert.Single(reports.Kills(warId, defender, WarReportGroup.Frigates));
            Assert.Equal(aggressorKill.Id, attackerEntry.Report.Id);
            Assert.Equal(allyKill.Id, defenderEntry.Report.Id);
            Assert.Equal(attackerEntry.IskLost, totals.IskKilled[attacker]);
            Assert.Equal(defenderEntry.IskLost, totals.IskKilled[defender]);
            Assert.Equal(allyKill.Id, Assert.Single(reports.Kills(warId, ally, null)).Report.Id);
            AssertDirectKillmail(thirdPartyKill);
            AssertDirectKillmail(friendlyKill);
        }

        void AssertDirectKillmail(ShipCombatReport expected)
        {
            var kill = Assert.IsType<CorporationWarKill>(reports.Kill(warId, expected.Id, WarReportRules.Hash(expected)));
            Assert.Equal(expected.FinalBlow, kill.Report.FinalBlow);
        }
    }

    [Fact]
    public void OpposingAllyDamageCountsOnlyWithinTheAllyAndWarWindows()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var database = new WorldDatabase(fixture.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var action = fixture.Services.GetRequiredService<LiveWorld>().Economy.Begin();
        const int attacker = 98000351;
        const int defender = 98000352;
        const int ally = 98000353;
        const int neutral = 98000354;
        long starts = clock.Now;
        long allyStarts = starts + TimeSpan.TicksPerHour;
        long allyEnds = starts + 2 * TimeSpan.TicksPerHour;
        action.Add(
            new SavedCorporationWar
            {
                Id = 1,
                AttackerId = attacker,
                DefenderId = defender,
                DeclaredAt = starts - TimeSpan.TicksPerDay,
                StartsAt = starts,
                EndsAt = allyEnds + TimeSpan.TicksPerDay,
            }
        );
        action.Add(
            new SavedCorporationWarAlly
            {
                WarId = 1,
                OwnerId = ally,
                AttackerId = attacker,
                StartsAt = allyStarts,
                EndsAt = allyEnds,
            }
        );
        CombatParty opposition = new(111, 511, ally, null, 601, null, null, 15);
        CombatParty final = new(112, 512, neutral, null, 601, null, null, 25);
        CombatParty victim = new(113, 513, attacker, null, 601, null, null, 40);
        ShipCombatReport report = new(1, 513, allyStarts, 30000142, victim, final, [opposition], []);
        Assert.Null(CorporationWarStore.WarForLoss(action, catalog, report with { Time = allyStarts - 1 }));
        Assert.Null(CorporationWarStore.WarForLoss(action, catalog, report with { Time = allyEnds }));
        Assert.Null(CorporationWarStore.WarForLoss(action, catalog, report with { OtherAttackers = [] }));
        var loss = Assert.IsType<CorporationWarStore.LossWar>(CorporationWarStore.WarForLoss(action, catalog, report));
        Assert.Equal((1L, attacker, true), (loss.Id, loss.VictimOrganizationId, loss.AttackerLoss));

        var reverse = report with { Victim = opposition, OtherAttackers = [victim] };
        var allyLoss = Assert.IsType<CorporationWarStore.LossWar>(CorporationWarStore.WarForLoss(action, catalog, reverse));
        Assert.Equal((1L, ally, false), (allyLoss.Id, allyLoss.VictimOrganizationId, allyLoss.AttackerLoss));
    }
}
