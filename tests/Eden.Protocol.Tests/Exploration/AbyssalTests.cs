using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.Information;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
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
    [Fact]
    public void DirectionalScannerRejectsAbyssalSpaceAndResumesAfterReturn()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadInformation(f.CatalogPath);
        Prepare(f);
        long ship = f.ShipId;
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, cargo: true);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using var scope = f.Db.Services.CreateScope();
        const long account = 1000001;
        var pilot = f.Db.Store.FindPilot(account)!;
        using PilotCallContext context = new(
            account,
            pilot.Account,
            [pilot],
            selected => new FlightSession(f.Db.Store, selected, f.Clock, new(), []),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var abyss = scope.ServiceProvider.GetRequiredService<IAbyssalDeadspace>();
        var systems = scope.ServiceProvider.GetRequiredService<ISystemObjects>();

        context.Flight.Snapshot();
        Assert.IsType<DirectionalScanReply>(Assert.IsType<PilotCallResult.Success>(Scan()).Reply);
        f.Clock.AdvanceWallTime(InformationRules.DirectionalScanCooldown);
        Assert.Same(PilotCallResult.None, Call("abyssalMgr", "AbyssalEntranceDeployment", filament, (long)AbyssalMode.Cruiser));
        context.Flight.Snapshot();
        Assert.True(AbyssalRules.IsSystem(context.Pilot.SystemId));
        var failure = Assert.IsType<PilotCallResult.Failure>(Scan());
        Assert.Equal("Directional scan is unavailable inside Abyssal Deadspace.", failure.Message);

        for (int room = 0; room < AbyssalRules.RoomCount; room++)
        {
            var run = abyss.Read(f.PilotId)!;
            KillRoom(f, live, run.Id);
            f.Db.Store.SetPosition(ship, AbyssalRules.ConduitPosition(room), run.SystemId);
            var moved = abyss.Activate(f.PilotId, ship, run.ConduitId);
            int system = moved.Pilot.SystemId;
            context.Flight.RelocateAbyssal(moved.Pilot, AbyssalRules.IsSystem(system) ? abyss.Objects(system) : systems.Load(system));
            context.ReleaseLocationReferences();
        }

        context.Flight.Snapshot();
        Assert.Equal(pilot.SystemId, context.Pilot.SystemId);
        Assert.IsType<DirectionalScanReply>(Assert.IsType<PilotCallResult.Success>(Scan()).Reply);

        PilotCallResult Call(string service, string method, params object?[] args) => router.Call(context, service, method, args, _ => { });

        PilotCallResult Scan()
        {
            var binding = Assert.IsType<BlueObjectReference>(
                Assert.IsType<PilotCallResult.Success>(Call("scanMgr", "GetSystemScanMgr")).Reply.ToBlue()
            );
            string service = router.ResolveService(context, [1L, (long)PilotLogin.NodeId, null, 0L], binding.Id);
            return Call(service, "ConeScan", Math.Tau, InformationRules.MaximumDirectionalRange, 1d, 0d, 0d);
        }
    }

    [Fact]
    public void ExpiredAbyssalRunStopsAdvertisingItsTraceAndActiveStateBeforeSettlement()
    {
        using Fixture f = new();
        Prepare(f);
        int origin = f.Db.Store.FindPilot(1000001)!.SystemId;
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, cargo: true);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        var run = abyss.Deploy(f.PilotId, f.ShipId, filament, AbyssalMode.Cruiser).Run;
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(run.ExpiresAt - f.Clock.Now - 1));
        Assert.Equal(AbyssalRules.Trace(run.Id), Assert.Single(abyss.Objects(origin)).Id);
        Assert.Equal(run, abyss.Read(f.PilotId));

        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));

        Assert.Empty(abyss.Objects(origin));
        Assert.Null(abyss.Read(f.PilotId));
        Assert.Equal((int)AbyssalStatus.Active, live.Economy.Inspect(world => world.Set<SavedAbyssalRun>().Find(run.Id)!.Status));
        abyss.Advance();
        Assert.Equal((int)AbyssalStatus.Failed, live.Economy.Inspect(world => world.Set<SavedAbyssalRun>().Find(run.Id)!.Status));
    }

    [Fact]
    public void CurrentDungeonUsesTheSavedRoomBeforeBallparkBinding()
    {
        using Fixture f = new();
        Prepare(f);
        long ship = f.ShipId;
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, 1, cargo: true);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        Assert.Null(ReadCurrentDungeon(f, f.Db.Store.FindPilot(1000001)!));

        var entered = abyss.Deploy(f.PilotId, ship, filament, AbyssalMode.Cruiser);
        AssertRoom(entered);
        KillRoom(f, live, entered.Run.Id);
        f.Db.Store.SetPosition(ship, AbyssalRules.ConduitPosition(0), entered.Run.SystemId);
        AssertRoom(abyss.Activate(f.PilotId, ship, entered.Run.ConduitId));

        void AssertRoom(AbyssalTransition transition)
        {
            var scene = AbyssalRules.Scene(transition.Run.Room);
            var result = BlueRead.Tuple(ReadCurrentDungeon(f, transition.Pilot), 4);
            Assert.Equal((long)scene.DungeonId, result[0]);
            Assert.Equal((long)scene.RoomId, result[1]);
            Assert.Equal(transition.Run.Id, result[2]);
            Assert.Empty(BlueRead.Fields(result[3]));
        }
    }

    internal static object? ReadCurrentDungeon(Fixture f, Eden.Gameplay.Docking.DockedPilot pilot)
    {
        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), f.Db.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Same(PilotCallResult.Unsupported, Call([]));
        context.Select(pilot);
        Assert.False(context.Flight.InBallpark);
        Assert.IsType<PilotCallResult.Failure>(Call([pilot.PilotId]));
        var reply = Assert.IsType<PilotCallResult.Success>(Call([])).Reply;
        Assert.Empty(context.Notifications);
        return BlueMarshal.Decode(BlueMarshal.Encode(reply));

        PilotCallResult Call(object?[] args) => router.Call(context, "keeper", "GetCurrentDungeonForCharacter", args, _ => { });
    }

    internal static void Prepare(Fixture f)
    {
        PassiveCatalogFixture.LoadAbyssal(f.CatalogPath);
        int origin = f.Db.Store.FindPilot(1000001)!.SystemId;
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems',{origin.ToString()},'{{\"securityStatus\":0.5}}')"
            );
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
        Assert.Equal(0, live.Economy.Inspect(world => world.Set<SavedCombatDamage>().Count()));
        await live.FlushAsync();
        using var disk = WorldConnections.FileConnection(f.Db.Path);
        using WorldDbContext saved = new(disk);
        var completed = Assert.Single(saved.Set<SavedAbyssalRun>());
        Assert.Equal((int)AbyssalStatus.Completed, completed.Status);
        Assert.Equal(origin.SystemId, saved.Positions.Find(ship)!.SystemId);
        Assert.Equal(3, saved.Set<OwnedItem>().Where(i => i.LocationId == ship && i.TypeId == 56132).Sum(i => i.Quantity));
        Assert.Equal(3, saved.Set<WorldWreck>().Count());
        Assert.Empty(saved.Set<SavedCombatDamage>());
        Assert.Equal(6, saved.Set<OwnedItem>().Where(i => i.LocationId == ship && i.TypeId == 48112).Sum(i => i.Quantity));
    }

    internal static void KillRoom(Fixture f, LiveWorld live, long run)
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
        var environment = Assert.Single(context.Notifications.OfType<EnteringAbyssalDungeon>());
        Assert.Equal(6204, environment.Arguments[0]);
        Assert.Equal(24754, environment.Arguments[1]);
        Assert.Null(environment.Arguments[3]);
        Assert.Null(environment.Arguments[4]);
        Assert.Contains(context.Notifications, n => n is AbyssalDeadline d && d.EndsAt == run.ExpiresAt);
        Assert.False(Assert.Single(abyss.Objects(run.SystemId), o => o.Abyssal is { Environment: false }).Abyssal!.Open);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        Assert.InRange(encounters.ReadScene(run.SystemId, AbyssalRules.Center(0), f.Clock.Now).Visible.Count, 4, 5);
        _ = abyss.Objects(pilot.SystemId);
        using var measured = SessionPerformance.Begin("Abyssal observer", _ => { }, force: true)!;
        for (int read = 0; read < 3; read++)
        {
            Assert.Equal(run, abyss.Read(f.PilotId));
            Assert.Equal(3, abyss.Objects(run.SystemId).Count);
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
