using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using CombatFixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class NpcMiningTests
{
    [Fact]
    public void EveryMiningHullPreservesItsDiamondNameAsNativeUnicode()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        var miner = Read(db, Clock().Now)[0];
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        foreach (int type in NpcMiningRules.Types)
        {
            string name = catalog.Set<ItemType>().Single(t => t.Id == type).Name;
            Assert.StartsWith("\u2666 ", name);
            var slim = new NpcSlimItem(miner with { TypeId = type, Name = name });
            var decoded = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(slim)));
            var fields = Assert.IsType<Dictionary<string, object?>>(decoded.State);
            Assert.Equal(name, Assert.IsType<string>(fields["name"]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionKeepsInboundResponseAggressionAndCanTargetTheArrivedShip(bool live)
    {
        using CombatFixture f = new(liveWorld: live, useTestClock: true);
        Prepare(f.Db);
        SeedDirect(f.Db, f.Clock.Now);
        var miner = Read(f.Db, f.Clock.Now)[0];
        f.Db.Store.SetPosition(ShipId, miner.Position + new SpacePosition(1000, 0, 0), SystemId);
        using var scope = f.Db.Services.CreateScope();
        SharedSpace space = new();
        SharedTargetLocks locks = new();
        using NpcCombatHandler handler = new(
            f.Clock,
            f.Combat,
            locks,
            scope.ServiceProvider.GetRequiredService<ShipLossCoordinator>(),
            space
        );
        using TargetingHandler targeting = new(f.Clock, f.Db.Services.GetRequiredService<IPilotFitting>(), locks);
        using PilotCallContext context = new(
            1000001,
            f.Pilot.Account,
            [f.Pilot],
            p => new(f.Db.Store, p, f.Clock, space, [], health: f.Health, encounters: f.Db.Services.GetRequiredService<INpcEncounters>()),
            42,
            SignedSettings.Empty
        );
        context.Select(f.Pilot);
        context.Flight.Snapshot();
        handler.Enter(context);
        f.Weapons.Fire(PilotId, f.Module, miner.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
        long arrival = long.MaxValue;
        long defender = 0;
        for (int second = 1; second <= 120; second++)
        {
            f.Clock.Seconds = second;
            SeedDirect(f.Db, f.Clock.Now);
            context.Flight.Checkpoint();
            context.Flight.RefreshNpcs();
            handler.Advance(context);
            targeting.Advance(context);
            using WorldDbContext world = new(f.Db.Path);
            var response = world.Set<WorldNpc>().Where(n => n.Role == NpcRole.MiningDefender).OrderBy(n => n.Id).ToArray();
            if (response.Length == 0)
            {
                continue;
            }
            if (defender == 0)
            {
                defender = response.First(n => n.TypeId == 43563).Id;
                arrival = world.Set<WorldNpcMiningMember>().Find(defender)!.WarpArrivesAt!.Value;
                Assert.DoesNotContain(context.Flight.VisibleNpcs, n => n.Id == defender);
            }
            Assert.Equal(7, world.Set<NpcAggression>().Count());
            Assert.All(world.Set<NpcAggression>(), a => Assert.Equal(ShipId, a.TargetShipId));
            if (f.Clock.Now < arrival)
            {
                Assert.False(locks.Contains(defender, ShipId));
                Assert.Empty(world.Set<WorldMissile>().Where(m => m.SourceShipId == defender));
                continue;
            }
            var contact = Assert.Single(context.Flight.VisibleNpcs, n => n.Id == defender);
            Assert.Null(contact.Warp);
            Assert.True(contact.Position.DistanceTo(context.Pilot.Position!) < 10_000);
            Assert.True(locks.Contains(defender, ShipId));
            Assert.Contains(
                context.Notifications.OfType<TargetChanged>(),
                n => n.TargetId == defender && n.Change == TargetChange.OtherAdded
            );
            var result = Assert.IsType<PilotCallResult.Success>(targeting.Call(context, "dogmaIM", "AddTarget", [defender]));
            Assert.True(Assert.IsType<TargetLockReply>(result.Reply).Pending);
            var missile = Assert.Single(world.Set<WorldMissile>().Where(m => m.SourceShipId == defender));
            Assert.Equal(ShipId, missile.TargetId);
            handler.Leave();
            Assert.Empty(f.Combat.Aggressors(ShipId));
            return;
        }
        Assert.Fail("The response did not arrive and engage within its expected transit.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObserversReceiveActiveWarpStateAndAuthoritativeLandingOnce(bool live)
    {
        var clock = Clock();
        using TestWorld db = new(clock: clock, regionalNpcs: true, liveWorld: live);
        Prepare(db);
        var miners = Read(db, clock.Now);
        db.Store.SetPosition(ShipId, miners[0].Position + new SpacePosition(1000, 0, 0), SystemId);
        SharedSpace space = new();
        using FlightSession flight = new(
            db.Store,
            db.Store.FindPilot(1000001)!,
            clock,
            space,
            [],
            encounters: db.Services.GetRequiredService<INpcEncounters>()
        );
        flight.Snapshot();
        using (WorldDbContext world = new(db.Path))
        {
            NpcMiningSeeder.Alert(world, world.Set<WorldNpc>().Find(miners[0].Id)!, ShipId, clock.Now);
            world.SaveChanges();
        }
        flight.Updates.Clear();
        clock.Seconds = 1;
        flight.RefreshNpcs();
        var departure = flight.Updates.Select(u => u.Command).OfType<DestinyCommand.AddBalls>().ToArray();
        Assert.Equal(3, departure.Length);
        Assert.All(departure.SelectMany(s => s.Addition.Slims).OfType<NpcSlimItem>(), n => Assert.NotNull(n.Npc.Warp));
        Assert.DoesNotContain(flight.Updates, u => u.Command is DestinyCommand.SetState or DestinyCommand.RemoveBall);
        // This read has no shared motion stamp. The wire must rebase the
        // contact's relative warp age to the observer's Destiny clock.
        AssertWarpStamp(flight.VisibleNpcs[0], flight.CurrentStamp);
        Assert.Null(flight.FindTarget(miners[0].Id));
        flight.Updates.Clear();
        flight.RefreshNpcs();
        Assert.Empty(flight.Updates);
        // Follow the response through deceleration, while miners leave this grid.
        long defender = 0;
        bool sawLanding = false;
        for (int second = 2; second <= 120; second++)
        {
            clock.Seconds = second;
            flight.Updates.Clear();
            flight.RefreshNpcs();
            var response = flight.VisibleNpcs.FirstOrDefault(n => n.TypeId == 43563);
            if (response is null)
            {
                continue;
            }
            defender = response.Id;
            if (response.Warp is not null)
            {
                AssertWarpStamp(response, flight.CurrentStamp);
                Assert.Null(flight.FindTarget(defender));
                continue;
            }
            var landing = flight.Updates.Select(u => u.Command).OfType<DestinyCommand.AddBalls>().SelectMany(a => a.Addition.Slims);
            var landed = Assert.Single(landing.OfType<NpcSlimItem>(), n => n.Npc.Id == defender).Npc;
            Assert.DoesNotContain(flight.Updates, u => u.Command is DestinyCommand.SetState);
            Assert.Equal(response.Position, landed.Position);
            Assert.Null(landed.Warp);
            Assert.Equal(response.Position, flight.FindTarget(defender)!.Position);
            Assert.Contains(
                flight.Updates,
                u => u.Command is DestinyCommand.SpecialEffect e && e.Id == defender && e.Graphic == "effects.Warping" && !e.Start
            );
            sawLanding = true;
            break;
        }
        Assert.True(defender != 0 && sawLanding);
        flight.Updates.Clear();
        flight.RefreshNpcs();
        Assert.Empty(flight.Updates);
    }

    private static void AssertWarpStamp(NpcContact npc, int stamp)
    {
        // Full-state header + free-ball common fields, through formation ID.
        const int WarpFieldsOffset = 105;
        using MemoryStream stream = new(Ballpark.AddNpcs([npc], stamp).State);
        using BinaryReader reader = new(stream);
        stream.Position = WarpFieldsOffset;
        Assert.Equal(npc.Warp!.Destination, new SpacePosition(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()));
        int started = reader.ReadInt32();
        Assert.True(started >= 0);
        Assert.Equal(stamp + npc.Warp.StartStamp, started);
        Assert.Equal(npc.Warp.Distance, reader.ReadDouble());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyHaulerTurnaroundReplacesItsInboundWarpWithoutRemovingTheShip(bool live)
    {
        var clock = Clock();
        long started = clock.Now;
        using TestWorld db = new(clock: clock, regionalNpcs: true, liveWorld: live);
        Prepare(db);
        var miner = Read(db, clock.Now)[0];
        db.Store.SetPosition(ShipId, miner.Position + new SpacePosition(1000, 0, 0), SystemId);
        using FlightSession flight = new(
            db.Store,
            db.Store.FindPilot(1000001)!,
            clock,
            new(),
            [],
            encounters: db.Services.GetRequiredService<INpcEncounters>()
        );
        flight.Snapshot();
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<WorldNpcMiningOperation>().Single().PickupAt = clock.Now + TimeSpan.TicksPerSecond;
            world.SaveChanges();
        }
        clock.Seconds = 1;
        var hauler = Read(db, clock.Now).Single(n => n.Role == NpcRole.MiningHauler);
        long arrival;
        using (WorldDbContext world = new(db.Path))
        {
            arrival = world.Set<WorldNpcMiningMember>().Find(hauler.Id)!.WarpArrivesAt!.Value;
        }
        clock.Milliseconds = (arrival - started) / TimeSpan.TicksPerMillisecond - 1000;
        flight.RefreshNpcs();
        var inbound = Assert.Single(flight.VisibleNpcs, n => n.Id == hauler.Id);
        Assert.NotNull(inbound.Warp);
        flight.Updates.Clear();
        clock.Seconds++;
        flight.RefreshNpcs();
        var replacement = flight.Updates.Select(u => u.Command).OfType<DestinyCommand.AddBalls>().SelectMany(a => a.Addition.Slims);
        var outbound = Assert.Single(replacement.OfType<NpcSlimItem>(), n => n.Npc.Id == hauler.Id).Npc;
        Assert.NotNull(outbound.Warp);
        Assert.NotEqual(inbound.Warp.Destination, outbound.Warp.Destination);
        Assert.DoesNotContain(
            flight.Updates,
            u => u.Command is DestinyCommand.SetState or DestinyCommand.RemoveBall or DestinyCommand.WarpTo
        );
        Assert.Null(flight.FindTarget(hauler.Id));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(NpcMiningTravel.Outbound, saved.Set<WorldNpcMiningMember>().Find(hauler.Id)!.Travel);
        Assert.Equal(0, saved.Set<WorldNpcMiningCargo>().Sum(c => c.Quantity));
    }
}
