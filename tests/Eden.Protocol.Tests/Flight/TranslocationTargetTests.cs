using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class TranslocationTests
{
    private const long RuntimeTarget = 7_000_000_000_001;
    private static readonly SpacePosition s_runtimePosition = new(80_000_000, 20_000_000, 30_000_000);

    [Fact]
    public void RuntimeResolverUsesTheLatestResidentDronePosition()
    {
        using Combat.CombatDroneTests.Fixture f = new();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        f.Launch(quantity: 1);
        var before = Assert.Single(f.Drones.List(OtherSystem, f.Now));
        f.Tick();
        var drone = Assert.Single(f.Drones.List(OtherSystem, f.Now));
        Assert.NotEqual(before.Position, drone.Position);
        var resolved = f.Base.Db.Services.GetRequiredService<IPilotTranslocation>().Resolve(drone.Id);
        Assert.Equal(drone.Position, resolved.Position);
        Assert.Equal(drone.Profile.Radius, resolved.Radius);
        Assert.Equal(drone.SystemId, resolved.System.Id);
    }

    [Theory]
    [InlineData("npc")]
    [InlineData("wreck")]
    [InlineData("asteroid")]
    [InlineData("drone")]
    public async Task RuntimeObjectDisappearanceDuringHandoffStillArrivesAtTheRequestedPosition(string kind)
    {
        using Fixture f = new(inSpace: true);
        long id = SeedRuntimeTarget(f, kind, docked: false);
        f.Select();
        f.Context.Flight.Snapshot();
        var target = f.Db.Services.GetRequiredService<IPilotTranslocation>().Resolve(id);
        var expected = TranslocationRules.Arrival(
            target,
            f.Context.Pilot.Flight.ShipRadius,
            f.Db.Services.GetRequiredService<ISystemObjects>().Load(OtherSystem)
        );
        Assert.Equal(PilotCallResult.None, f.Call($"/tr me {id}"));
        using (WorldDbContext world = new(f.Db.Path))
        {
            switch (kind)
            {
                case "npc":
                    world.Set<WorldNpc>().Remove(world.Set<WorldNpc>().Find(id)!);
                    break;
                case "wreck":
                    world.Set<WorldWreck>().Remove(world.Set<WorldWreck>().Find(id)!);
                    break;
                case "asteroid":
                    world.Set<WorldAsteroid>().Remove(world.Set<WorldAsteroid>().Find(id)!);
                    break;
                case "drone":
                    world.Set<WorldDrone>().Remove(world.Set<WorldDrone>().Find(id)!);
                    world.Set<OwnedItem>().Remove(world.Set<OwnedItem>().Find(id)!);
                    break;
            }
            world.SaveChanges();
        }
        f.Clock.Seconds += FlightSession.TranslocationDurationTicks;
        await f.TickAsync([]);
        Assert.Equal(expected, f.Context.Pilot.Position);
        Assert.Equal(OtherSystem, f.Context.Pilot.SystemId);
        Assert.Equal(f.Context.Pilot, f.Db.Store.FindPilot(1000001));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PilotDestinationsWorkFromStationAndSpaceAndDockWithTheDestinationPilot(bool inSpace, bool dockedTarget)
    {
        using Fixture f = new(inSpace: inSpace);
        long id = SeedRuntimeTarget(f, "pilot", dockedTarget);
        if (!dockedTarget)
        {
            f.Db.Store.SetPosition(f.Db.Store.FindPilot(1000001)!.ShipId, inSpace ? new(70_000_000, 0, 0) : null, OtherSystem);
        }
        f.Select();
        if (inSpace)
        {
            f.Context.Flight.Snapshot();
        }
        var peer = f.Db.Store.FindPilot(1000002)!;
        Assert.Equal(PilotCallResult.None, f.Call($"/tr me {id}"));
        Assert.False(f.Context.Flight.Translocating);
        Assert.Equal(peer.SystemId, f.Context.Pilot.SystemId);
        Assert.Equal(dockedTarget, f.Context.Pilot.Position is null);
        Assert.Equal(peer, f.Db.Store.FindPilot(1000002));
        Assert.Equal(f.Context.Pilot, f.Db.Store.FindPilot(1000001));
    }

    [Theory]
    [InlineData("pilot", false)]
    [InlineData("pilot", true)]
    [InlineData("ship", false)]
    [InlineData("ship", true)]
    [InlineData("npc", false)]
    [InlineData("wreck", false)]
    [InlineData("asteroid", false)]
    [InlineData("drone", false)]
    public void RuntimeDestinationsPreserveBothPilotsAndInventoryAndReopenAtTheResolvedLocation(string kind, bool docked)
    {
        using Fixture f = new();
        long id = SeedRuntimeTarget(f, kind, docked);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var peer = f.Db.Store.FindPilot(1000002)!;
        string items = Items(f.Db);
        var travel = f.Db.Services.GetRequiredService<IPilotTranslocation>();
        var target = travel.Resolve(id);
        Assert.Equal(id, target.Id);
        Assert.Equal(docked ? HomeStationTestData.System : OtherSystem, target.System.Id);
        Assert.Equal(docked ? HomeStationTestData.School : null, target.StationId);
        if (!docked)
        {
            Assert.Equal(s_runtimePosition, target.Position);
            Assert.True(target.Radius > 0);
        }
        var moved = travel.Move(pilot.PilotId, pilot.ShipId, id);
        Assert.Equal(pilot.ShipId, moved.Pilot.ShipId);
        Assert.Equal(target.System.Id, moved.Pilot.SystemId);
        Assert.Equal(
            docked
                ? null
                : TranslocationRules.Arrival(
                    target,
                    pilot.Flight.ShipRadius,
                    f.Db.Services.GetRequiredService<ISystemObjects>().Load(OtherSystem)
                ),
            moved.Pilot.Position
        );
        Assert.Equal(peer, f.Db.Store.FindPilot(1000002));
        Assert.Equal(items, Items(f.Db));
        Assert.Equal(moved.Pilot, WorldInitialization.Open(f.Db.Path).FindPilot(1000001));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeShipAndPilotIdsUseTheNativeHandoffAndRetainTheRequestedDestination(bool usePilotId)
    {
        using Fixture f = new(inSpace: true);
        long shipId = SeedRuntimeTarget(f, "ship", docked: false);
        long id = usePilotId ? f.Db.Store.FindPilot(1000002)!.PilotId : shipId;
        f.Select();
        f.Context.Flight.Snapshot();
        var travel = f.Db.Services.GetRequiredService<IPilotTranslocation>();
        var target = travel.Resolve(id);
        var expected = TranslocationRules.Arrival(
            target,
            f.Context.Pilot.Flight.ShipRadius,
            f.Db.Services.GetRequiredService<ISystemObjects>().Load(OtherSystem)
        );
        Assert.Equal(PilotCallResult.None, f.Call($"/tr me {id}"));
        Assert.True(f.Context.Flight.Translocating);
        // The destination ship changes system during the three-second camera
        // handoff. The request must retain the scene already sent to the client.
        f.Db.Store.SetPosition(shipId, new(90_000_000, 0, 0), HomeStationTestData.System);
        var peer = f.Db.Store.FindPilot(1000002)!;
        f.Clock.Seconds += FlightSession.TranslocationDurationTicks;
        await f.TickAsync([]);
        Assert.False(f.Context.Flight.Translocating);
        Assert.Equal(OtherSystem, f.Context.Pilot.SystemId);
        Assert.Equal(expected, f.Context.Pilot.Position);
        Assert.Equal(peer, f.Db.Store.FindPilot(1000002));
        Assert.Equal(f.Context.Pilot, f.Db.Store.FindPilot(1000001));
        f.Context.Flight.Snapshot();
        Assert.Contains(f.Context.Flight.Updates, u => u.Command is DestinyCommand.SpecialEffect { Graphic: "effects.JumpDriveIn" });
    }

    [Theory]
    [InlineData("npc")]
    [InlineData("retired npc")]
    [InlineData("wreck")]
    [InlineData("asteroid")]
    [InlineData("drone")]
    [InlineData("stored ship")]
    public void UnavailableRuntimeTargetsRejectWithoutMovingOrCancellingNavigation(string kind)
    {
        using Fixture f = new(inSpace: true);
        string seed =
            kind == "retired npc" ? "npc"
            : kind == "stored ship" ? "ship"
            : kind;
        long id = SeedRuntimeTarget(f, seed, docked: false);
        using (WorldDbContext world = new(f.Db.Path))
        {
            switch (kind)
            {
                case "npc":
                    world.Set<WorldNpc>().Find(id)!.DestroyedAt = f.Clock.Now;
                    break;
                case "retired npc":
                    world.Set<WorldNpc>().Find(id)!.RetiredAt = f.Clock.Now;
                    break;
                case "wreck":
                    world.Set<WorldWreck>().Find(id)!.ExpiresAt = f.Clock.Now;
                    break;
                case "asteroid":
                    world.Set<WorldAsteroid>().Find(id)!.Quantity = 0;
                    break;
                case "drone":
                    world.Set<WorldDrone>().Find(id)!.SystemId = null;
                    break;
                case "stored ship":
                    world.Positions.Remove(world.Positions.Find(id)!);
                    world.Ships.Find(id)!.StoredInId = f.Db.Store.FindPilot(1000001)!.ShipId;
                    break;
            }
            world.SaveChanges();
        }
        f.Select();
        f.Context.Flight.Snapshot();
        Assert.Equal(PilotCallResult.None, f.Context.Flight.Execute(new FlightCommand.Heading(1, 0, 0)));
        var before = f.Context.Pilot;
        string items = Items(f.Db);
        Assert.IsType<PilotCallResult.Failure>(f.Call($"/tr me {id}"));
        Assert.True(f.Context.Flight.Moving);
        Assert.False(f.Context.Flight.Translocating);
        Assert.Equal(before, f.Context.Pilot);
        Assert.Equal(before, f.Db.Store.FindPilot(1000001));
        Assert.Equal(items, Items(f.Db));
    }

    private static long SeedRuntimeTarget(Fixture f, string kind, bool docked)
    {
        var peer = f.Db.Store.FindPilot(1000002)!;
        if (kind is "pilot" or "ship")
        {
            f.Db.Store.SetPosition(peer.ShipId, docked ? null : s_runtimePosition, OtherSystem);
            if (docked)
            {
                using WorldDbContext dock = new(f.Db.Path);
                dock.Ships.Find(peer.ShipId)!.StationId = HomeStationTestData.School;
                dock.SaveChanges();
            }
            return kind == "pilot" ? peer.PilotId : peer.ShipId;
        }
        using WorldDbContext world = new(f.Db.Path);
        switch (kind)
        {
            case "npc":
                world.Add(
                    new WorldNpc
                    {
                        Id = RuntimeTarget,
                        TypeId = peer.ShipTypeId,
                        SystemId = OtherSystem,
                        X = s_runtimePosition.X,
                        Y = s_runtimePosition.Y,
                        Z = s_runtimePosition.Z,
                    }
                );
                break;
            case "wreck":
                world.Add(
                    new WorldNpc
                    {
                        Id = RuntimeTarget - 1,
                        TypeId = peer.ShipTypeId,
                        SystemId = OtherSystem,
                        DestroyedAt = f.Clock.Now,
                    }
                );
                world.Add(
                    new WorldWreck
                    {
                        Id = RuntimeTarget,
                        TypeId = peer.ShipTypeId,
                        SystemId = OtherSystem,
                        OwnerId = peer.PilotId,
                        NpcId = RuntimeTarget - 1,
                        Name = "Test wreck",
                        ExpiresAt = f.Clock.Now + TimeSpan.TicksPerHour,
                        X = s_runtimePosition.X,
                        Y = s_runtimePosition.Y,
                        Z = s_runtimePosition.Z,
                    }
                );
                break;
            case "asteroid":
                world.Add(
                    new WorldAsteroid
                    {
                        Id = RuntimeTarget,
                        TypeId = peer.ShipTypeId,
                        SystemId = OtherSystem,
                        Quantity = 1,
                        Radius = 100,
                        X = s_runtimePosition.X,
                        Y = s_runtimePosition.Y,
                        Z = s_runtimePosition.Z,
                    }
                );
                break;
            case "drone":
                world.Add(
                    new OwnedItem
                    {
                        Id = RuntimeTarget,
                        TypeId = peer.ShipTypeId,
                        OwnerId = peer.PilotId,
                        LocationId = OtherSystem,
                        FlagId = (int)InventoryFlag.AutoFit,
                        Quantity = 1,
                        Singleton = true,
                    }
                );
                world.Add(
                    new WorldDrone
                    {
                        ItemId = RuntimeTarget,
                        PilotId = peer.PilotId,
                        ControllerShipId = peer.ShipId,
                        SystemId = OtherSystem,
                        X = s_runtimePosition.X,
                        Y = s_runtimePosition.Y,
                        Z = s_runtimePosition.Z,
                    }
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        world.SaveChanges();
        return RuntimeTarget;
    }
}
