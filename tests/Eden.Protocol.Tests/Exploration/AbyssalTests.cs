using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class AbyssalTests
{
    private static void Prepare(Fixture f)
    {
        PassiveCatalogFixture.LoadAbyssal(f.CatalogPath);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT INTO SolarSystem VALUES (32000001,'AD001',20000020),(32000002,'AD002',20000020)");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.ShipId)!.TypeId = 621;
            world.SaveChanges();
        }
        f.Db.Store.SetPosition(f.ShipId, new SpacePosition(2_000_000_000, 0, 0));
    }

    [Fact]
    public async Task ThreeRoomsRequireKillsAndReturnWithCheckpointedRewards()
    {
        using Fixture f = new();
        Prepare(f);
        long ship = f.ShipId;
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, 2, cargo: true);
        var origin = f.Db.Store.FindPilot(1000001)!;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        var entered = abyss.Deploy(f.PilotId, ship, filament, AbyssalMode.Cruiser);
        Assert.Equal(32000001, entered.Pilot.SystemId);
        long deadline = f.Clock.Now + AbyssalRules.Duration;
        Assert.Equal(deadline, entered.Run.ExpiresAt);
        Assert.Throws<InvalidDataException>(() => abyss.Deploy(f.PilotId, ship, filament, AbyssalMode.Cruiser));
        for (int room = 0; room < AbyssalRules.RoomCount; room++)
        {
            var run = abyss.Read(f.PilotId)!;
            Assert.Equal(room, run.Room);
            Assert.Equal(deadline, run.ExpiresAt);
            f.Db.Store.SetPosition(ship, AbyssalRules.ConduitPosition(room), run.SystemId);
            Assert.Throws<InvalidDataException>(() => abyss.Activate(f.PilotId, ship, run.ConduitId));
            KillRoom(f, live, run.Id);
            var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
            var wreck = Assert.Single(wrecks.List(run.SystemId, f.Clock.Now));
            f.Db.Store.SetPosition(ship, wreck.Position, run.SystemId);
            var contents = wrecks.Read(f.PilotId, wreck.Id, f.Clock.Now);
            wrecks.Take(f.PilotId, ship, wreck.Id, contents.Items.Select(i => i.Id).ToArray(), null, f.Clock.Now);
            f.Db.Store.SetPosition(ship, AbyssalRules.ConduitPosition(room), run.SystemId);
            var moved = abyss.Activate(f.PilotId, ship, run.ConduitId);
            Assert.Throws<InvalidDataException>(() => abyss.Activate(f.PilotId, ship, run.ConduitId));
            Assert.Equal(room == 2 ? origin.SystemId : run.SystemId, moved.Pilot.SystemId);
        }
        Assert.Null(abyss.Read(f.PilotId));
        await live.FlushAsync();
        using var disk = WorldConnections.FileConnection(f.Db.Path);
        using WorldDbContext saved = new(disk);
        var completed = Assert.Single(saved.Set<SavedAbyssalRun>());
        Assert.Equal((int)AbyssalStatus.Completed, completed.Status);
        Assert.Equal(origin.SystemId, saved.Positions.Find(ship)!.SystemId);
        Assert.Equal(4, saved.Set<OwnedItem>().Where(i => i.LocationId == ship && i.TypeId == 56132).Sum(i => i.Quantity));
        Assert.Equal(3, saved.Set<WorldWreck>().Count());
        Assert.Equal(6, saved.Set<OwnedItem>().Where(i => i.LocationId == ship && i.TypeId == 48112).Sum(i => i.Quantity));
    }

    private static void KillRoom(Fixture f, LiveWorld live, long run)
    {
        using var transaction = live.Economy.Begin(new(pilots: [f.PilotId]));
        using CatalogDbContext catalog = new(f.CatalogPath);
        var pilot = transaction.Pilots.Find(f.PilotId)!;
        foreach (
            var npc in transaction
                .Filter<WorldNpc>(n =>
                    n.EncounterId == run && n.AnchorKind == NpcAnchorKind.Abyssal && n.DestroyedAt == null && n.RetiredAt == null
                )
                .ToArray()
        )
        {
            var profile = new NpcProfileCatalog().Read(catalog, npc.TypeId);
            var damage = NpcDamagePersistence.Apply(
                transaction,
                catalog,
                npc,
                pilot,
                pilot.ActiveShipId,
                profile.Attributes,
                new DamageVector(1_000_000, 0, 0, 0),
                f.Clock.Now
            );
            Assert.True(damage.Destroyed);
            Assert.Null(damage.Reward);
        }
        transaction.Commit();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void RejectedModeDoesNotConsumeFilament(int mode)
    {
        using Fixture f = new();
        Prepare(f);
        long ship = f.ShipId;
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, cargo: true);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        Assert.Throws<InvalidDataException>(() => abyss.Deploy(f.PilotId, ship, filament, (AbyssalMode)mode));
        Assert.Null(abyss.Read(f.PilotId));
        Assert.Equal(1, live.Economy.Inspect(w => w.Set<OwnedItem>().Find(filament)!.Quantity));
    }

    [Fact]
    public void NativeDeploymentAndReadyPublishRoomAndAbsoluteDeadlineWithoutWarmSql()
    {
        using Fixture f = new();
        Prepare(f);
        long ship = f.ShipId;
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, cargo: true);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        var objects = f.Db.Services.GetRequiredService<ISystemObjects>();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            f.Db.Store.ReadAccount(1000001),
            [pilot],
            selected => new FlightSession(f.Db.Store, selected, f.Clock, new(), []),
            1,
            new([], [])
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        AbyssalHandler handler = new(abyss, objects);
        Assert.Equal(PilotCallResult.None, handler.Call(context, "abyssalMgr", "AbyssalEntranceDeployment", [filament, 1L]));
        var run = abyss.Read(f.PilotId)!;
        Assert.Equal(run.SystemId, context.Pilot.SystemId);
        Assert.Contains(context.Notifications, n => n is AbyssalLocationEntered);
        Assert.Contains(context.Notifications, n => n is AbyssalDeadline d && d.EndsAt == run.ExpiresAt);
        Assert.False(Assert.Single(abyss.Objects(run.SystemId)).Abyssal!.Open);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        Assert.Equal(2, encounters.ReadScene(run.SystemId, AbyssalRules.Center(0), f.Clock.Now).Visible.Count);
        _ = abyss.Objects(pilot.SystemId);
        using var measured = SessionPerformance.Begin("Abyssal observer", _ => { }, force: true)!;
        for (int read = 0; read < 3; read++)
        {
            Assert.Equal(run, abyss.Read(f.PilotId));
            Assert.Single(abyss.Objects(run.SystemId));
            Assert.Single(abyss.Objects(pilot.SystemId));
            Assert.Equal(PilotCallResult.None, handler.Call(context, "abyssalMgr", "ClientIsReady", []));
        }
        Assert.Equal(0, measured.Commands);
        Assert.Equal(0, measured.Connections);
        Assert.Equal(0, measured.FittingCalculations);
        Assert.Equal(new object?[] { run.Id, 11 }, new AbyssalFinished(run.Id).Arguments);
    }

    [Fact]
    public void DisconnectKeepsShipPresentUntilOriginalDeadline()
    {
        using Fixture f = new();
        Prepare(f);
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, cargo: true);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        var run = abyss.Deploy(f.PilotId, f.ShipId, filament, AbyssalMode.Cruiser).Run;
        var crimewatch = f.Db.Services.GetRequiredService<IPilotCrimewatch>();
        crimewatch.Disconnect(f.PilotId, f.ShipId);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        Assert.Equal(run.ExpiresAt, Assert.Single(crimewatch.LoggedOff(run.SystemId)).ExpiresAt);
        Assert.Equal(run.ExpiresAt, abyss.Read(f.PilotId)!.ExpiresAt);
    }

    [Fact]
    public async Task ExpiredRunDestroysShipAndCapsuleOnlyOnceAfterCheckpointReopen()
    {
        using Fixture f = new();
        Prepare(f);
        long ship = f.ShipId;
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, cargo: true);
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
            abyss.Deploy(f.PilotId, ship, filament, AbyssalMode.Cruiser);
            await live.FlushAsync();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(AbyssalRules.Duration));
        using (LiveWorld reopened = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
            abyss.Advance();
            Assert.Null(abyss.Read(f.PilotId));
            var replacement = f.Db.Store.FindPilot(1000001)!;
            Assert.Null(replacement.Position);
            Assert.NotEqual(ship, replacement.ShipId);
            abyss.Advance();
            Assert.Equal(replacement.ShipId, f.Db.Store.FindPilot(1000001)!.ShipId);
            await reopened.FlushAsync();
        }
    }
}
