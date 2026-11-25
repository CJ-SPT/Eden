using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.Protocol.Fighters;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Structures.StructureDefenseFixture;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed partial class StandupFighterTests
{
    private const int StandupTemplar = 47035;
    private const int StandupGram = 47036;
    private const int StandupSiren = 47037;
    private const int StandupGungnir = 47039;
    private const int Keepstar = 35834;
    private const long MobilitySlot = FighterMobilityRules.Slot;

    [Theory]
    [InlineData(StandupTemplar, 6, 6, 1, 20_000, false)]
    [InlineData(StandupGram, 3, .2, .5, 10_000, false)]
    [InlineData(StandupSiren, 3, 1, 1, 20_000, true)]
    public void GunnerActivatesNativeStandupFighterMobilityUntilExpiryOrStop(
        int type,
        float speed,
        double signature,
        double shield,
        int duration,
        bool stopEarly
    )
    {
        using Fixture f = new(liveWorld: true);
        var (structure, squadron) = PrepareMobility(f, type);
        using var scope = f.Db.Services.CreateScope();
        using var gunner = Session(f, scope, 1000001, 101);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var drones = scope.ServiceProvider.GetRequiredService<IPilotDrones>();
        var deck = scope.ServiceProvider.GetRequiredService<IPilotFighterDeck>();
        PilotCallResult Call(string service, string method, object?[] args) => router.Call(gunner, service, method, args, _ => { });
        Assert.Same(PilotCallResult.None, Call("structureControl", "TakeControl", [structure.Id]));
        Assert.IsType<PilotCallResult.Success>(
            Call("fighterMgr", "LaunchFightersFromTubes", [new object?[] { (long)FighterDeckRules.FirstTube }])
        );
        var normal = Contact();
        object?[] slots = [new object?[] { squadron }, MobilitySlot];
        Assert.IsType<PilotCallResult.Success>(Call("fighterMgr", "CmdActivateAbilitySlots", slots));
        Assert.IsType<PilotCallResult.Failure>(Call("fighterMgr", "CmdActivateAbilitySlots", slots));
        var active = Contact();
        Assert.True(active.MobilityActive);
        Assert.Equal(normal.Profile.MaximumSpeed * speed, active.Profile.MaximumSpeed);
        Assert.Equal(signature, active.MobilitySignatureMultiplier, 8);
        Assert.Equal(normal.Health.Defense.ShieldResonance.Em * shield, active.Health.Defense.ShieldResonance.Em, 8);
        Assert.Contains(gunner.NotificationsBeforeFlight.OfType<FighterMobilityStarted>(), started => started.Squadron == squadron);
        var cycle = Assert.Single(deck.Read(f.PilotId, structure.Id).Squadrons).Mobility!;
        Assert.Equal(TimeSpan.FromMilliseconds(duration).Ticks, cycle.EndsAt - cycle.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, cycle.ReadyAt - cycle.EndsAt);
        if (stopEarly)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            Assert.IsType<PilotCallResult.Success>(Call("fighterMgr", "CmdDeactivateAbilitySlots", slots));
            cycle = Assert.Single(deck.Read(f.PilotId, structure.Id).Squadrons).Mobility!;
        }
        else
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(cycle.EndsAt - f.Clock.Now - 1));
            Assert.True(Contact().MobilityActive);
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
            scope.ServiceProvider.GetRequiredService<FighterDeckHandler>().Advance(gunner);
        }
        Assert.Contains(gunner.NotificationsBeforeFlight.OfType<FighterMobilityEnded>(), ended => ended.Squadron == squadron);
        Assert.False(Contact().MobilityActive);
        Assert.Equal(normal.Profile.MaximumSpeed, Contact().Profile.MaximumSpeed);
        Assert.Equal(normal.Health.Defense.ShieldResonance, Contact().Health.Defense.ShieldResonance);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(cycle.ReadyAt - f.Clock.Now - 1));
        Assert.IsType<PilotCallResult.Failure>(Call("fighterMgr", "CmdActivateAbilitySlots", slots));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.IsType<PilotCallResult.Success>(Call("fighterMgr", "CmdActivateAbilitySlots", slots));

        DroneContact Contact() => Assert.Single(drones.List(structure.SystemId, f.Clock.Now), row => row.Id == squadron);
    }

    [Fact]
    public void StandupFighterMicroJumpCompletesOnceAndReleaseCancelsPendingJump()
    {
        using Fixture f = StructureAttackTests.Prepare();
        var (structure, squadron) = PrepareMobility(f, StandupGungnir, Keepstar);
        using var scope = f.Db.Services.CreateScope();
        using var gunner = Session(f, scope, 1000001, 101);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var fighters = scope.ServiceProvider.GetRequiredService<IStandupFighterFlight>();
        var drones = scope.ServiceProvider.GetRequiredService<IPilotDrones>();
        PilotCallResult Call(string service, string method, object?[] args) => router.Call(gunner, service, method, args, _ => { });
        Assert.Same(PilotCallResult.None, Call("structureControl", "TakeControl", [structure.Id]));
        Assert.IsType<PilotCallResult.Success>(
            Call("fighterMgr", "LaunchFightersFromTubes", [new object?[] { (long)FighterDeckRules.FirstTube }])
        );
        var lease = gunner.StructureControl!.Token;
        var origin = Contact().Position;
        var destination = origin + new SpacePosition(0, 90_000, 0);
        Assert.IsType<PilotCallResult.Failure>(Jump(origin + new SpacePosition(0, 100_001, 0)));
        Assert.IsType<PilotCallResult.Success>(Jump(destination));
        var active = Contact().Mobility!;
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, active.EndsAt - active.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(90).Ticks, active.ReadyAt - active.EndsAt);
        Assert.Equal(2.5, Contact().MobilitySignatureMultiplier);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(active.EndsAt - f.Clock.Now - 1));
        fighters.AdvanceReturns(f.Clock.Now);
        Assert.Equal(origin, Contact().Position);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        fighters.AdvanceReturns(f.Clock.Now);
        Assert.Equal(destination, Contact().Position);
        Assert.Equal(f.Clock.Now, Contact().Mobility!.JumpedAt);
        Assert.Null(Contact().Mobility!.Destination);
        Assert.Equal(1, Contact().MobilitySignatureMultiplier);
        gunner.Flight.RefreshDrones();
        Assert.Contains(gunner.Flight.Updates, update => update.Command is DestinyCommand.SetBallPosition move && move.Id == squadron);
        fighters.AdvanceReturns(f.Clock.Now);
        Assert.Equal(destination, Contact().Position);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(active.ReadyAt - f.Clock.Now));
        Assert.IsType<PilotCallResult.Success>(Jump(origin));
        var pending = Contact().Mobility!;
        Assert.Same(PilotCallResult.None, Call("structureControl", "ReleaseControl", []));
        Assert.Equal(DroneActivity.ReturningToBay, Contact().Activity);
        Assert.Null(Contact().Mobility!.Destination);
        Assert.Throws<InvalidDataException>(() => fighters.SetMobility(lease, [squadron], true, f.Clock.Now, origin));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(pending.EndsAt - f.Clock.Now));
        fighters.AdvanceReturns(f.Clock.Now);
        Assert.NotEqual(origin, Contact().Position);
        Assert.Equal(pending.ReadyAt, Contact().Mobility!.ReadyAt);

        DroneContact Contact() => Assert.Single(drones.List(structure.SystemId, f.Clock.Now), row => row.Id == squadron);
        PilotCallResult Jump(SpacePosition point) =>
            Call(
                "fighterMgr",
                "CmdActivateAbilitySlots",
                [new object?[] { squadron }, MobilitySlot, new object?[] { point.X, point.Y, point.Z }]
            );
    }

    private static (StructureSnapshot Structure, long Squadron) PrepareMobility(Fixture f, int type, int structureType = 35832)
    {
        string catalogPath = new WorldDatabase(f.Db.Path).CatalogPath;
        PassiveCatalogFixture.LoadCapitalTravel(catalogPath);
        if (structureType == Keepstar)
        {
            using CatalogDbContext catalog = new(catalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw("UPDATE ItemType SET Capacity=1000000 WHERE Id=601");
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000 WHERE ItemTypeId=601 AND AttributeId=38");
        }
        var structure = f.Online(structureType);
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        using var action = LiveWorld.Find(f.Db.Path)!.Economy.Begin(new(pilots: [f.PilotId], ships: [structure.Id]));
        long id = action.NextItemId();
        action.Add(
            new OwnedItem
            {
                Id = id,
                OwnerId = f.CorporationId,
                LocationId = structure.Id,
                FlagId = FighterDeckRules.FirstTube,
                TypeId = type,
                Quantity = 3,
            }
        );
        action.Commit();
        return (structure, id);
    }
}
