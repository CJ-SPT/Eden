using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.FactionWarfare;
using Eden.Data.World.Market;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.FactionWarfare;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Combat;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.FactionWarfare;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed partial class MilitiaHubTests
{
    private const int System = 30002087;
    private const int Neighbor = 30002084;
    private const int MinmatarOpen = 9668;
    private const int AmarrOpen = 9666;

    [Fact]
    public async Task DefendingRepairsTheHubAndOneFinalBlowSchedulesAReopenedOccupationFlip()
    {
        using NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        var clock = fixture.Clock;
        var database = new WorldDatabase(fixture.Db.Path);
        MilitiaComplexTests.LoadComplexCatalog(database);
        LoadHubCatalog(database);
        var services = fixture.Db.Services;
        var encounters = services.GetRequiredService<INpcEncounters>();
        var warzones = services.GetRequiredService<IFactionWarzones>();
        var complexes = services.GetRequiredService<IMilitiaComplexes>();
        var live = services.GetRequiredService<LiveWorld>();
        var pilot = fixture.Pilot;
        var wing = fixture.Db.Store.FindPilot(1000002)!;

        Assert.DoesNotContain(encounters.List(pilot.SystemId, clock.Now), npc => npc.Role == NpcRole.MilitiaHub);
        var hub = Hub();
        Assert.Equal(MilitiaHubRules.TypeId(MilitiaFaction.Minmatar), hub.TypeId);
        Assert.Equal(MilitiaHubRules.GroupId, hub.GroupId);
        Assert.Equal(7_500_000, hub.Health.Remaining.Shield);
        Assert.Equal(7_500_000, hub.Health.Remaining.Armor);
        Assert.Equal(10_000_000, hub.Health.Remaining.Structure);
        Assert.Equal(
            hub.Id,
            encounters.ReadScene(System, SpacePosition.Zero, clock.Now).Visible.Single(n => n.Role == NpcRole.MilitiaHub).Id
        );
        fixture.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, System);
        using (var flight = new FlightSession(fixture.Db.Store, fixture.Pilot, clock, new(), [], encounters: encounters))
        {
            flight.Snapshot();
            Assert.IsType<PilotCallResult.Success>(flight.Execute(new FlightCommand.WarpTo(hub.Id, 0)));
            Assert.True(flight.PreparingWarp || flight.InWarp);
        }
        var initialMap = warzones.Systems();
        Assert.Equal(WarzoneAdjacency.CommandOperations, warzones.System(Neighbor)!.Adjacency);
        var oldOpen = complexes.Sites(System, clock.Now).Single(s => s.DungeonId == MinmatarOpen);

        fixture.Db.Store.SetPosition(pilot.ShipId, hub.Position + new SpacePosition(11_000, 0, 0), System);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = pilot.PilotId, DirectFaction = MilitiaFaction.Amarr });
            action.Add(new SavedMilitiaEnlistment { PilotId = wing.PilotId, DirectFaction = MilitiaFaction.Minmatar });
            action.Commit();
        }
        Assert.Contains("vulnerable", Assert.Throws<InvalidDataException>(() => Fire(hub.Id)).Message);
        Assert.Null(live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(fixture.Module)));
        SetPoints(FactionWarzoneRules.VictoryPointThreshold - 1);
        Assert.Equal(MilitiaHubPhase.Contested, warzones.System(System)!.HubPhase);
        Assert.Contains("vulnerable", Assert.Throws<InvalidDataException>(() => Fire(hub.Id)).Message);

        var currentPilot = fixture.Pilot;
        using PilotCallContext context = new(
            1000001,
            currentPilot.Account,
            [currentPilot],
            p => new(fixture.Db.Store, p, clock, new(), [], encounters: encounters),
            42,
            SignedSettings.Empty
        );
        context.Select(currentPilot);
        var updates = new FactionWarzoneUpdates(warzones);
        updates.Advance(context);
        SetPoints(FactionWarzoneRules.VictoryPointThreshold + FactionWarzoneRules.VictoryPointBuffer);
        updates.Advance(context);
        var vp = Assert.Single(context.Notifications.OfType<WarzoneVictoryPointsUpdated>());
        Assert.Equal(MilitiaHubPhase.Vulnerable, vp.Current.HubPhase);
        Assert.Contains(context.Notifications, notice => notice.Name == "OnFacWarBunkerVulnerable");
        context.Notifications.Clear();

        Assert.True(Fire(hub.Id).Applied > 0);
        Assert.Equal(0, Deal(hub.Id, wing.PilotId, 50_000_000));
        Assert.Equal(1, Contributions(hub.Id));
        Assert.True(Defend(clock.Now, oldOpen));
        Assert.Equal(75_045, warzones.System(System)!.VictoryPoints);
        Assert.Equal(hub.Id, Hub().Id);
        Assert.Equal(1, Contributions(hub.Id));

        clock.Seconds += (long)MilitiaComplexRules.Replacement(warzones.System(System)!.Adjacency).TotalSeconds;
        var nextOpen = complexes.Sites(System, clock.Now).Single(s => s.DungeonId == MinmatarOpen);
        Assert.True(Defend(clock.Now, nextOpen));
        var defended = warzones.System(System)!;
        Assert.Equal(74_590, defended.VictoryPoints);
        Assert.Equal(MilitiaHubPhase.Contested, defended.HubPhase);
        var repaired = Hub();
        Assert.NotEqual(hub.Id, repaired.Id);
        Assert.Equal(repaired.Health.Defense.Undamaged, repaired.Health.Remaining);
        Assert.Equal(0, Contributions(hub.Id));
        Assert.Equal(0, Deal(hub.Id, pilot.PilotId, 50_000_000));
        Assert.Null(Loyalty(live, pilot.PilotId));
        updates.Advance(context);
        Assert.Contains(context.Notifications, notice => notice.Name == "OnFacWarBunkerNonVulnerable");
        context.Notifications.Clear();

        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Set<SavedMilitiaEnlistment>().Find(wing.PilotId)!.DirectFaction = MilitiaFaction.Amarr;
            action.Commit();
        }
        SetPoints(FactionWarzoneRules.VictoryPointThreshold);
        Assert.Equal(2_000, Deal(repaired.Id, wing.PilotId, 2_000));
        Assert.Equal(1, Contributions(repaired.Id));
        await live.FlushAsync();
        context.Dispose();
        fixture.Db.Services.Dispose();

        using var beforeFlip = ServerServices.Build(database, clock, liveWorld: true);
        live = beforeFlip.GetRequiredService<LiveWorld>();
        encounters = beforeFlip.GetRequiredService<INpcEncounters>();
        warzones = beforeFlip.GetRequiredService<IFactionWarzones>();
        complexes = beforeFlip.GetRequiredService<IMilitiaComplexes>();
        Assert.Equal(repaired.Id, Hub().Id);
        Assert.Equal(1, Contributions(repaired.Id));
        var hits = await Task.WhenAll(
            Task.Run(() => Deal(repaired.Id, pilot.PilotId, 50_000_000)),
            Task.Run(() => Deal(repaired.Id, pilot.PilotId, 50_000_000))
        );
        Assert.Single(hits, hit => hit > 0);
        Assert.Equal(20_000, Loyalty(live, pilot.PilotId));
        Assert.Equal(20_000, Loyalty(live, wing.PilotId));
        long transfer = MilitiaHubRules.NextTransfer(clock.Now);
        var captured = warzones.System(System)!;
        Assert.Equal(MilitiaHubPhase.Captured, captured.HubPhase);
        Assert.Equal(transfer, captured.PendingTransferAt);
        Assert.Equal(MilitiaFaction.Minmatar, captured.Occupier);
        Assert.Empty(complexes.Sites(System, clock.Now));
        Assert.DoesNotContain(encounters.ReadScene(System, SpacePosition.Zero, clock.Now).Visible, n => n.Role == NpcRole.MilitiaHub);
        await live.FlushAsync();
        beforeFlip.Dispose();

        using var afterReopen = ServerServices.Build(database, clock, liveWorld: true);
        live = afterReopen.GetRequiredService<LiveWorld>();
        warzones = afterReopen.GetRequiredService<IFactionWarzones>();
        encounters = afterReopen.GetRequiredService<INpcEncounters>();
        complexes = afterReopen.GetRequiredService<IMilitiaComplexes>();
        var maintenance = afterReopen.GetRequiredService<IMilitiaHubs>();
        Assert.Equal(captured, warzones.System(System));

        using PilotCallContext observer = new(
            1000001,
            currentPilot.Account,
            [currentPilot],
            p => new(fixture.Db.Store, p, clock, new(), []),
            43,
            SignedSettings.Empty
        );
        observer.Select(currentPilot);
        updates = new(warzones);
        updates.Advance(observer);
        maintenance.Sweep(transfer - 1);
        Assert.Equal(captured, warzones.System(System));
        maintenance.Sweep(transfer);
        maintenance.Sweep(transfer + 1);
        updates.Advance(observer);
        var flipped = warzones.System(System)!;
        Assert.Equal(MilitiaFaction.Amarr, flipped.Occupier);
        Assert.Equal(MilitiaHubPhase.Stable, flipped.HubPhase);
        Assert.Equal(transfer, flipped.CapturedAt);
        Assert.Equal(WarzoneAdjacency.Frontline, warzones.System(Neighbor)!.Adjacency);
        Assert.Equal(MilitiaFaction.Minmatar, initialMap.Single(s => s.SystemId == System).Occupier);

        var updated = observer.Notifications.OfType<WarzoneOccupationUpdated>().ToArray();
        var takeover = Assert.Single(updated, notice => notice.Current.SystemId == System);
        Assert.Contains(updated, notice => notice.Current.SystemId == Neighbor);
        var payload = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(takeover.Arguments)), 3);
        Assert.Equal((long)System, payload[0]);
        Assert.Equal((long)MilitiaFaction.Amarr, Assert.IsType<BlueConstructor>(payload[1]).Arguments[0]);
        Assert.Equal((long)MilitiaFaction.Minmatar, Assert.IsType<BlueConstructor>(payload[2]).Arguments[0]);
        var nextHub = Hub();
        Assert.Equal(MilitiaHubRules.TypeId(MilitiaFaction.Amarr), nextHub.TypeId);
        Assert.NotEqual(repaired.Id, nextHub.Id);
        Assert.Equal(nextHub.Health.Defense.Undamaged, nextHub.Health.Remaining);
        var newOpen = Assert.Single(complexes.Sites(System, transfer), site => site.DungeonId == AmarrOpen);
        Assert.NotEqual(oldOpen.Id, newOpen.Id);
        Assert.NotEqual(oldOpen.TargetId, newOpen.TargetId);
        Assert.Equal(0, Deal(repaired.Id, pilot.PilotId, 50_000_000));
        Assert.Equal(20_000, Loyalty(live, pilot.PilotId));

        using var metrics = SessionPerformance.Begin("warmed militia hub and occupation reads", _ => { }, force: true)!;
        var map = warzones.Systems();
        maintenance.Sweep(transfer + TimeSpan.TicksPerSecond);
        Assert.Equal(map, warzones.Systems());
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);

        NpcContact Hub() => Assert.Single(encounters.List(System, clock.Now), npc => npc.Role == NpcRole.MilitiaHub);

        CombatShot Fire(long id) => fixture.Weapons.Fire(pilot.PilotId, fixture.Module, id, SpacePosition.Zero, clock.Now, .5);

        long Contributions(long npc) =>
            live.Economy.Inspect(world => world.Filter<SavedMilitiaHubContribution>(c => c.NpcId == npc).LongCount());

        void SetPoints(int points)
        {
            using var action = live.Economy.Begin(TransactionParticipants.None);
            var saved =
                action.Set<SavedWarzoneOccupation>().Find(System)
                ?? action.Add(new SavedWarzoneOccupation { SystemId = System, Occupier = MilitiaFaction.Minmatar }).Entity;
            saved.VictoryPoints = points;
            action.Commit();
        }

        bool Defend(long start, Eden.Gameplay.Exploration.CombatAnomaly site)
        {
            var point = site.Objects.Single(body => body.MilitiaCapture is not null).Position;
            fixture.Db.Store.SetPosition(wing.ShipId, point, System);
            var participant = new MilitiaComplexPilot(wing.PilotId, wing.ShipId, point);
            complexes.Advance(System, [participant], start);
            using (var action = live.Economy.Begin(TransactionParticipants.None))
            {
                action.Set<SavedMilitiaComplex>().Find(MilitiaComplexRules.Slot(System, MilitiaComplexFamily.Open))!.Progress =
                    -MilitiaComplexRules.Policy(MilitiaComplexFamily.Open, 5).Duration + TimeSpan.TicksPerSecond;
                action.Commit();
            }
            clock.Seconds++;
            complexes.Advance(System, [participant], clock.Now);
            return complexes.Sites(System, clock.Now).All(current => current.Id != site.Id);
        }

        double Deal(long npc, long source, double damage) => Strike(database, live, npc, source, damage, clock.Now);
    }

    [Fact]
    public async Task SimultaneousDefenseAndFinalDamageHaveOneTerritorialOutcome()
    {
        TestClock clock = new(new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        MilitiaComplexTests.LoadComplexCatalog(database);
        LoadHubCatalog(database);
        var defender = fixture.Store.FindPilot(1000002)!;
        var attacker = fixture.Store.FindPilot(1000001)!;
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var warzones = fixture.Services.GetRequiredService<IFactionWarzones>();
        var sites = fixture.Services.GetRequiredService<IMilitiaComplexes>();
        var encounters = fixture.Services.GetRequiredService<INpcEncounters>();
        var hub = encounters.List(System, clock.Now).Single(npc => npc.Role == NpcRole.MilitiaHub);
        long slot = MilitiaComplexRules.Slot(System, MilitiaComplexFamily.Open);
        var site = sites.Sites(System, clock.Now).Single(s => s.DungeonId == MinmatarOpen);
        var point = site.Objects.Single(body => body.MilitiaCapture is not null).Position;
        fixture.Store.SetPosition(defender.ShipId, point, System);
        fixture.Store.SetPosition(attacker.ShipId, hub.Position + new SpacePosition(11_000, 0, 0), System);
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(new SavedMilitiaEnlistment { PilotId = defender.PilotId, DirectFaction = MilitiaFaction.Minmatar });
            action.Add(new SavedMilitiaEnlistment { PilotId = attacker.PilotId, DirectFaction = MilitiaFaction.Amarr });
            action.Add(
                new SavedWarzoneOccupation
                {
                    SystemId = System,
                    Occupier = MilitiaFaction.Minmatar,
                    VictoryPoints = FactionWarzoneRules.VictoryPointThreshold,
                }
            );
            action.Add(
                new SavedMilitiaComplex
                {
                    Slot = slot,
                    Occupier = MilitiaFaction.Minmatar,
                    Progress = -MilitiaComplexRules.Policy(MilitiaComplexFamily.Open, 5).Duration + TimeSpan.TicksPerSecond,
                }
            );
            action.Commit();
        }
        var occupant = new MilitiaComplexPilot(defender.PilotId, defender.ShipId, point);
        sites.Advance(System, [occupant], clock.Now);
        clock.Seconds++;
        long now = clock.Now;
        var shot = Task.Run(() => Strike(database, live, hub.Id, attacker.PilotId, 50_000_000, now));
        var finish = Task.Run(() => sites.Advance(System, [occupant], now));
        double applied = await shot;
        await finish;

        var system = warzones.System(System)!;
        if (applied > 0)
        {
            Assert.Equal(MilitiaHubPhase.Captured, system.HubPhase);
            Assert.Equal(FactionWarzoneRules.VictoryPointThreshold, system.VictoryPoints);
            Assert.Equal(MilitiaHubRules.BaseLoyaltyPoints, Loyalty(live, attacker.PilotId));
            Assert.Null(live.Economy.Inspect(w => w.Set<SavedLoyaltyBalance>().Find(defender.PilotId, MilitiaRules.TribalLiberationForce)));
        }
        else
        {
            Assert.Equal(MilitiaHubPhase.Contested, system.HubPhase);
            Assert.Equal(FactionWarzoneRules.VictoryPointThreshold - 455, system.VictoryPoints);
            Assert.Null(Loyalty(live, attacker.PilotId));
            Assert.NotEqual(hub.Id, encounters.List(System, now).Single(npc => npc.Role == NpcRole.MilitiaHub).Id);
        }
        Assert.Equal(0, Strike(database, live, hub.Id, attacker.PilotId, 50_000_000, now));
    }

    private static double Strike(WorldDatabase database, LiveWorld live, long npc, long source, double damage, long now) =>
        LiveWorld.Execute(
            database.Path,
            System,
            () =>
            {
                using var action = live.Economy.Begin(new(pilots: [source], resources: [new(SharedSimulationResource.Npc, npc)]));
                using CatalogDbContext catalog = new(database.CatalogPath);
                var target = action.Set<WorldNpc>().Find(npc);
                if (target is not { DestroyedAt: null, RetiredAt: null })
                {
                    return 0d;
                }
                var pilot = action.Set<Pilot>().Find(source)!;
                var result = NpcDamagePersistence.Apply(
                    action,
                    catalog,
                    target,
                    pilot,
                    pilot.ActiveShipId,
                    catalog.FittingAttributes(target.TypeId),
                    new(damage, 0, 0, 0),
                    now
                );
                action.Commit();
                return result.Applied;
            },
            "hub test damage"
        );

    private static long? Loyalty(LiveWorld live, long pilot) =>
        live.Economy.Inspect(world => world.Set<SavedLoyaltyBalance>().Find(pilot, MilitiaRules.ImperialCrusade)?.Points);

    private static void LoadHubCatalog(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (925,'FW Infrastructure Hub',11);
            INSERT INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES
                (29286,'Amarr Infrastructure Hub',925,5000000,10290),
                (29304,'Minmatar Infrastructure Hub',925,5000000,10290);
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES
                (9,'hp'),(263,'shieldCapacity'),(265,'armorHP'),(479,'shieldRechargeRate'),(552,'signatureRadius');
            INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES
                (29286,9,10000000),(29286,263,7500000),(29286,265,7500000),(29286,479,10800000),(29286,552,4000),
                (29304,9,10000000),(29304,263,7500000),(29304,265,7500000),(29304,479,10800000),(29304,552,4000);
            """
        );
    }
}
