using Eden.Data.World.Characters;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using CombatFixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class MilitiaFriendlyFireTests
{
    private const int SystemId = 30002087;
    private const int ScoutDungeon = 13351;
    private const long Attacker = CombatFixture.PilotId;
    private const long Victim = 90000002;
    private const long VictimShip = 1000000000002;
    private const MilitiaFaction Faction = MilitiaFaction.Minmatar;

    [Theory]
    [InlineData(false, -.125)]
    [InlineData(true, -.5)]
    public async Task FirstUnlawfulFriendlyFireRecordsGridPenaltyOnceAcrossRetryAndReopen(bool inside, double after)
    {
        using CombatFixture fixture = new(liveWorld: true, useTestClock: true);
        WorldDatabase database = new(fixture.Db.Path);
        MilitiaComplexTests.LoadComplexCatalog(database);
        var host = fixture.Db.Services;
        var live = host.GetRequiredService<LiveWorld>();
        var complexes = host.GetRequiredService<IMilitiaComplexes>();
        var site = Assert.Single(complexes.Sites(SystemId, fixture.Clock.Now), row => row.DungeonId == ScoutDungeon);
        var point = Assert.Single(site.Objects, row => row.MilitiaCapture is not null);
        SpacePosition origin = inside ? point.Position : site.Position;
        SpacePosition target = origin + new SpacePosition(500, 0, 0);
        fixture.Db.Store.SetPosition(CombatFixture.ShipId, origin, SystemId);
        fixture.Db.Store.SetPosition(VictimShip, target, SystemId);
        using (var world = live.Economy.Begin(new(pilots: [Attacker, Victim])))
        {
            world.Add(new SavedMilitiaEnlistment { PilotId = Attacker, DirectFaction = Faction });
            world.Add(new SavedMilitiaEnlistment { PilotId = Victim, DirectFaction = Faction });
            world.Commit();
        }

        var history = host.GetRequiredService<IPilotInteractionHistory>();
        complexes.Advance(SystemId, [new(Attacker, CombatFixture.ShipId, origin), new(Victim, VictimShip, target)], fixture.Clock.Now);
        fixture.Clock.Seconds++;
        complexes.Advance(SystemId, [new(Attacker, CombatFixture.ShipId, origin), new(Victim, VictimShip, target)], fixture.Clock.Now);
        Assert.Empty(history.Transactions(Attacker, (int)Faction));
        Assert.Throws<InvalidDataException>(() => Fire(fixture.Weapons));
        Assert.Empty(history.Transactions(Attacker, (int)Faction));
        host.GetRequiredService<IPilotSafety>().Set(Attacker, ShipSafetyLevel.Partial);

        using (var world = live.Economy.Begin(new(pilots: [Attacker, Victim])))
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            var rolledBack = CrimewatchPersistence.CheckPlayer(
                world,
                catalog,
                world.Pilots.Find(Attacker)!,
                world.Pilots.Find(Victim)!,
                SystemId,
                fixture.Clock.Now
            );
            CrimewatchPersistence.Player(world, catalog, Attacker, Victim, SystemId, rolledBack, fixture.Clock.Now, fixture.Clock.Now);
            Assert.Equal(after, world.Set<SavedNpcStanding>().Find(Attacker, (long)Faction)!.Value, 8);
        }
        Assert.Empty(history.Transactions(Attacker, (int)Faction));

        Fire(fixture.Weapons);
        var receipt = Assert.Single(history.Transactions(Attacker, (int)Faction));
        Assert.Equal(StandingEvent.CombatAggressionOwnFaction, receipt.Event);
        Assert.Equal((int)Faction, receipt.SourceId);
        Assert.Equal(SystemId, receipt.SystemId);
        Assert.Equal(0, receipt.Before);
        Assert.Equal(after, receipt.After, 8);
        await live.FlushAsync();
        host.Dispose();

        using var reopened = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        history = reopened.GetRequiredService<IPilotInteractionHistory>();
        fixture.Clock.Seconds += 10;
        Fire(reopened.GetRequiredService<IPilotCombat>());
        Assert.Equal(receipt, Assert.Single(history.Transactions(Attacker, (int)Faction)));

        live = reopened.GetRequiredService<LiveWorld>();
        using (var world = live.Economy.Begin(new(pilots: [Attacker, Victim])))
        using (CatalogDbContext catalog = new(database.CatalogPath))
        {
            var retaliation = CrimewatchPersistence.CheckPlayer(
                world,
                catalog,
                world.Pilots.Find(Victim)!,
                world.Pilots.Find(Attacker)!,
                SystemId,
                fixture.Clock.Now
            );
            Assert.Equal(new AggressionDecision(CrimewatchFlag.None, true), retaliation);
            CrimewatchPersistence.Player(world, catalog, Victim, Attacker, SystemId, retaliation, fixture.Clock.Now, fixture.Clock.Now);
            world.Commit();
        }
        Assert.Empty(history.Transactions(Victim, (int)Faction));

        using (var world = live.Economy.Begin(new(pilots: [Attacker, Victim])))
        {
            world.Set<SavedMilitiaEnlistment>().Find(Victim)!.DirectFaction = MilitiaFaction.Amarr;
            world.Commit();
        }
        reopened.GetRequiredService<IPilotSafety>().Set(Attacker, ShipSafetyLevel.Full);
        fixture.Clock.Seconds += 10;
        Fire(reopened.GetRequiredService<IPilotCombat>());
        Assert.Equal(receipt, Assert.Single(history.Transactions(Attacker, (int)Faction)));

        void Fire(IPilotCombat combat) =>
            combat.FirePlayerBank(
                Attacker,
                fixture.Module,
                new(VictimShip, SpacePosition.Zero),
                SpacePosition.Zero,
                fixture.Clock.Now,
                .999
            );
    }
}
