using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Structures;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Structures;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Structures;
using Eden.World.Catalog;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Structures.StructureDefenseFixture;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureWarpDisruptorTests
{
    private const DogmaEffect Effect = DogmaEffect.StructureWarpDisruptor;
    private const string NativeEffect = "structureWarpScrambleBlockMWDWithNPCEffect";
    private const int ScramblingScript = 47336;

    [Theory]
    [InlineData(35949, false, 210000, 4000)]
    [InlineData(35949, true, 63000, 4000)]
    [InlineData(47334, false, 300000, 4800)]
    [InlineData(47334, true, 90000, 4800)]
    public void DisruptorUsesCatalogRangeStrengthAndOnePaidCycle(int type, bool scripted, double range, double cost)
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, type);
        if (scripted)
        {
            LoadScript(f, structure.Id, module);
        }
        var control = f.Db.Services.GetRequiredService<IPilotStructureControl>();
        using var lease = control.Acquire(structure.Id, f.PilotId, 101);
        AddNpc(f, structure);
        Lock(f, lease.Token, Npc);
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        using (var action = StructureAttackTests.Scope(f))
        {
            var npc = action.World.Set<WorldNpc>().Find(Npc)!;
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
            npc.X = structure.Position.X + structure.Profile.Radius + (catalog.RequireItem(npc.TypeId).Radius ?? 0) + range + 1;
            action.Commit();
        }
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, module, Npc, 1000, 1, Effect));
        using (var action = StructureAttackTests.Scope(f))
        {
            Assert.Null(action.World.Set<SavedStructureEnergy>().Find(structure.Id));
            action.World.Set<WorldNpc>().Find(Npc)!.X -= 2;
            action.Commit();
        }
        var activation = weapons.Activate(lease.Token, module, Npc, 1000, 1, Effect);
        var tackle = Assert.IsType<TackleActivation>(activation.Tackle);
        Assert.Equal(cost, tackle.Module.CapacitorNeed);
        Assert.Equal(range, tackle.Module.Range, 6);
        Assert.Equal(100, tackle.Module.Strength);
        Assert.Equal(scripted ? 1 : 0, tackle.Module.MwdBlock);
        Assert.Equal(0, activation.Cycle.Repeats);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, activation.Cycle.ReadyAt - activation.Cycle.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, activation.Cycle.ReactivationAt - activation.Cycle.ReadyAt);
        Assert.Null(weapons.Activate(lease.Token, module, Npc, 0, 1, Effect).Tackle);
        if (scripted)
        {
            var magazine = Assert.Single(f.Db.Services.GetRequiredService<IPilotStructureAmmunition>().Read(f.PilotId, structure.Id));
            Assert.Equal(ScramblingScript, magazine.TypeId);
            Assert.Equal(1, magazine.Quantity);
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(90));
        var late = weapons.Advance(lease.Token, 1);
        Assert.Single(late.Stopped);
        Assert.Empty(late.Started);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDisruptorBlocksWarpAndUpwellDockingAndScriptAddsPropulsionBlock(bool scripted)
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, 35949);
        if (scripted)
        {
            LoadScript(f, structure.Id, module);
        }
        var other = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(
            other.ShipId,
            structure.Position + new SpacePosition(structure.Profile.Radius + 1000, 0, 0),
            Fixture.SystemId
        );
        using var peerScope = f.Db.Services.CreateScope();
        using var peer = Session(f, peerScope, 1000002, 102);
        peer.Flight.Snapshot();
        using var scope = f.Db.Services.CreateScope();
        using var gunner = Session(f, scope, 1000001, 101);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(string service, string method, object?[] args) => router.Call(gunner, service, method, args, _ => { });
        Assert.IsType<PilotCallResult.Success>(Call("structureControl", "TakeControl", [structure.Id]));
        Lock(f, gunner.StructureControl!.Token, other.ShipId);

        object?[] activate = [module, NativeEffect, other.ShipId, 0L];
        Assert.IsType<PilotCallResult.Failure>(Call("dogmaIM", "Activate", activate));
        using (var retaliation = StructureAttackTests.Scope(f))
        {
            StructureCombatTargets.Record(retaliation.World, other.ShipId, structure.Id, f.Clock.Now, f.Clock.Now);
            retaliation.Commit();
        }
        Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "Activate", activate));
        var dogma = Assert.IsType<DogmaAllInfo>(
            Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "GetAllInfo", [true, true, true])).Reply
        );
        Assert.Contains(Effect, dogma.ShipInfo[module].ActiveEffects.Keys);
        var receiver = peerScope.ServiceProvider.GetRequiredService<TackleHandler>();
        receiver.Advance(peer);
        var tackle = peerScope.ServiceProvider.GetRequiredService<IPilotTackle>();
        Assert.Equal(100, tackle.ShipAttributes(other.PilotId)[DogmaAttributeId.WarpScrambleStatus]);
        Assert.Equal(scripted, peer.Flight.MicrowarpdriveBlocked);
        var began = Assert.Single(peer.Notifications.OfType<TackleEwarChanged>());
        Assert.Equal(new object?[] { structure.Id, module, other.ShipId, "warpScramblerMWD" }, began.Arguments);
        Assert.True(began.Active);
        long destination = f.Db.Objects.First(body => body.DestinationGateId.HasValue).Id;
        var warp = Assert.IsType<PilotCallResult.Failure>(peer.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", destination]));
        Assert.Contains("Warp disruption", warp.Message);

        f.Structures.SetAccess(f.PilotId, structure.Id, true, []);
        var docking = f.Db.Services.GetRequiredService<IStationDocking>();
        var denied = Assert.Throws<InvalidDataException>(() => docking.Dock(other.PilotId, other.ShipId, structure.Id));
        Assert.Contains("warp disruption", denied.Message);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        receiver.Advance(peer);
        Assert.Equal(0, tackle.ShipAttributes(other.PilotId).GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus));
        Assert.False(peer.Flight.MicrowarpdriveBlocked);
        Assert.Contains(peer.Notifications.OfType<TackleEwarChanged>(), e => !e.Active);
        Assert.Equal(structure.Id, docking.Dock(other.PilotId, other.ShipId, structure.Id).DockedLocationId);
    }

    private static void LoadScript(Fixture f, long structure, long module)
    {
        var ammunition = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
        ammunition.Load(f.PilotId, structure, [module], [f.Item(ScramblingScript)], f.ShipId);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(1));
    }
}
