using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
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
