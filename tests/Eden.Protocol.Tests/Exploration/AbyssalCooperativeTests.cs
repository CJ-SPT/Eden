using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class AbyssalCooperativeTests
{
    [Fact]
    public void EveryFilamentMatchesPinnedWeatherAndTierAndRewardsProgress()
    {
        using Fixture f = new();
        AbyssalTests.Prepare(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.Equal(35, AbyssalContent.Filaments.Count());
        foreach (var filament in AbyssalContent.Filaments)
        {
            var dogma = catalog.FittingAttributes(filament.TypeId);
            Assert.Equal(filament.Tier, dogma[(DogmaAttributeId)2761]);
            int weather = filament.Weather switch
            {
                AbyssalWeather.Dark => 47863,
                AbyssalWeather.Electrical => 47865,
                AbyssalWeather.Exotic => 47862,
                AbyssalWeather.Firestorm => 47864,
                _ => 47866,
            };
            Assert.Equal(weather, dogma[(DogmaAttributeId)2760]);
            var rewards = AbyssalContent.Rewards(filament.Tier, AbyssalMode.Frigates, filament.Weather, 2, 1);
            Assert.Contains(
                rewards,
                r => AbyssalContent.Filaments.Any(next => next.TypeId == r.TypeId && next.Tier == Math.Min(6, filament.Tier + 1))
            );
            Assert.All(rewards, r => Assert.NotNull(catalog.FittingType(r.TypeId)));
        }
        Assert.Equal(5, Enumerable.Range(0, 20).Select(AbyssalContent.DataSiteFilament).OfType<int>().Distinct().Count());
    }

    [Theory]
    [InlineData(AbyssalMode.Frigates, 587, 3)]
    [InlineData(AbyssalMode.Destroyers, 16242, 2)]
    public async Task CooperativeTraceSharesDeadlineAndAllowsIndependentCheckpointedRooms(AbyssalMode mode, int type, int cost)
    {
        using Fixture f = new();
        AbyssalTests.Prepare(f);
        var owner = f.Db.Store.FindPilot(1000001)!;
        var mate = f.Db.Store.FindPilot(1000002)!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(owner.ShipId)!.TypeId = type;
            world.Ships.Find(mate.ShipId)!.TypeId = type;
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(mate.ShipId, owner.Position, owner.SystemId);
        var fleets = f.Db.Services.GetRequiredService<ICooperativeFleets>();
        foreach (var pilot in new[] { owner, mate })
        {
            fleets.Update(Guid.NewGuid(), new(pilot.PilotId, pilot.CorpId, pilot.ShipId, type, owner.SystemId, owner.Position));
        }
        var fleet = fleets.Create(owner.PilotId);
        fleets.Invite(owner.PilotId, fleet.Id, mate.PilotId, new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member));
        fleets.Accept(mate.PilotId, fleet.Id);
        long filament = f.Grant(56132, cost + 1, cargo: true);
        long runId;
        long deadline;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
            var deployed = abyss.Deploy(owner.PilotId, owner.ShipId, filament, mode);
            Assert.Equal(AbyssalStatus.Deployed, deployed.Run.Status);
            Assert.Equal(owner.SystemId, deployed.Pilot.SystemId);
            Assert.Equal(1, live.Economy.Inspect(w => w.Set<OwnedItem>().Find(filament)!.Quantity));
            runId = deployed.Run.Id;
            f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
            var first = abyss.Activate(owner.PilotId, owner.ShipId, AbyssalRules.Trace(runId));
            deadline = f.Clock.Now + AbyssalRules.Duration;
            Assert.Equal(deadline, first.Run.ExpiresAt);
            var second = abyss.Activate(mate.PilotId, mate.ShipId, AbyssalRules.Trace(runId));
            Assert.Equal(deadline, second.Run.ExpiresAt);
            AbyssalTests.KillRoom(f, live, runId);
            f.Db.Store.SetPosition(owner.ShipId, AbyssalRules.ConduitPosition(0), first.Run.SystemId);
            abyss.Activate(owner.PilotId, owner.ShipId, first.Run.ConduitId);
            Assert.Equal(1, abyss.Read(owner.PilotId)!.Room);
            Assert.Equal(0, abyss.Read(mate.PilotId)!.Room);
            Assert.Equal(6, abyss.Objects(first.Run.SystemId).Count);
            await live.FlushAsync();
        }
        // No live fleet state is needed for already admitted members after restart.
        fleets.Disband(owner.PilotId, fleet.Id);
        using (LiveWorld reopened = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
            var run = abyss.Read(mate.PilotId)!;
            Assert.Equal(deadline, run.ExpiresAt);
            f.Db.Store.SetPosition(mate.ShipId, AbyssalRules.ConduitPosition(0), run.SystemId);
            abyss.Activate(mate.PilotId, mate.ShipId, run.ConduitId);
            Assert.Equal(1, abyss.Read(mate.PilotId)!.Room);
            Assert.Equal(3, abyss.Objects(run.SystemId).Count);
            for (int room = 1; room < 3; room++)
            {
                AbyssalTests.KillRoom(f, reopened, runId);
                foreach (var pilot in new[] { owner, mate })
                {
                    f.Db.Store.SetPosition(pilot.ShipId, AbyssalRules.ConduitPosition(room), run.SystemId);
                    abyss.Activate(pilot.PilotId, pilot.ShipId, AbyssalRules.Conduit(runId, room));
                }
            }
            Assert.Null(abyss.Read(owner.PilotId));
            Assert.Null(abyss.Read(mate.PilotId));
            Assert.Equal(owner.SystemId, f.Db.Store.FindPilot(1000002)!.SystemId);
            Assert.Equal((int)AbyssalStatus.Completed, reopened.Economy.Inspect(w => w.Set<SavedAbyssalRun>().Find(runId)!.Status));
            await reopened.FlushAsync();
        }
    }

    [Fact]
    public async Task UnusedCooperativeTraceExpiresWithoutDestroyingShipsAfterReopen()
    {
        using Fixture f = new();
        AbyssalTests.Prepare(f);
        long ship = f.ShipId;
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(ship)!.TypeId = 587;
            world.SaveChanges();
        }
        var fleets = f.Db.Services.GetRequiredService<ICooperativeFleets>();
        fleets.Update(Guid.NewGuid(), new(f.PilotId, pilot.CorpId, ship, 587, pilot.SystemId, pilot.Position));
        fleets.Create(f.PilotId);
        long filament = f.Grant(56132, 3, cargo: true);
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
            abyss.Deploy(f.PilotId, ship, filament, AbyssalMode.Frigates);
            Assert.Throws<InvalidDataException>(() => abyss.Deploy(f.PilotId, ship, filament, AbyssalMode.Frigates));
            await live.FlushAsync();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(AbyssalRules.TraceDuration));
        using LiveWorld reopened = new(new(f.Db.Path), f.Clock, _ => { });
        var store = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        store.Advance();
        store.Advance();
        Assert.Empty(store.Objects(pilot.SystemId));
        Assert.Equal(ship, f.Db.Store.FindPilot(1000001)!.ShipId);
        Assert.Empty(reopened.Economy.Inspect(w => w.Filter<ShipLoss>(_ => true).ToArray()));
        Assert.Null(reopened.Economy.Inspect(w => w.Set<OwnedItem>().Find(filament)));
        await reopened.FlushAsync();
    }

    [Fact]
    public void HigherTierSpawnsPinnedOvermindAndPvpRejectionPreservesInventory()
    {
        using Fixture f = new();
        AbyssalTests.Prepare(f);
        long ship = f.ShipId;
        long filament = f.Grant(56139, cargo: true);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using (var tx = live.Economy.Begin(new(pilots: [f.PilotId])))
        {
            tx.Add(new SavedCrimewatch { PilotId = f.PilotId, PvpUntil = f.Clock.Now + TimeSpan.TicksPerMinute });
            tx.Commit();
        }
        var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        Assert.Throws<InvalidDataException>(() => abyss.Deploy(f.PilotId, ship, filament, AbyssalMode.Cruiser));
        Assert.Equal(1, live.Economy.Inspect(w => w.Set<OwnedItem>().Find(filament)!.Quantity));
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        var result = abyss.Deploy(f.PilotId, ship, filament, AbyssalMode.Cruiser);
        Assert.Equal(6, result.Run.Tier);
        Assert.Equal(AbyssalWeather.Electrical, result.Run.Weather);
        Assert.Contains(
            live.Economy.Inspect(w => w.Filter<WorldNpc>(n => n.EncounterId == result.Run.Id).Select(n => n.TypeId).ToArray()),
            t => t == 56213
        );
    }
}
