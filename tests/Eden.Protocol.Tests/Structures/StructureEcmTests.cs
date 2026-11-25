using Eden.Data.World.Combat;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Structures.StructureDefenseFixture;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureEcmTests
{
    private const DogmaEffect Effect = DogmaEffect.StructureEcm;
    private const string NativeEffect = "structureModuleEffectECM";

    [Theory]
    [InlineData(35940, 75000, 3000, 30)]
    [InlineData(47338, 90000, 3600, 36)]
    [InlineData(46577, 97500, 3000, 39)]
    public void EcmUsesCatalogScriptAndPaysOncePerCycle(int type, double range, double cost, double strength)
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, type);
        var ammunition = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
        const int gravimetricScript = 37821;
        ammunition.Load(f.PilotId, structure.Id, [module], [f.Item(gravimetricScript)], f.ShipId);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(1));
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        AddNpc(f, structure);
        Lock(f, lease.Token, Npc);

        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        double capacity = structure.Fitting!.Attributes[DogmaAttributeId.CapacitorCapacity];
        var first = weapons.Activate(lease.Token, module, Npc, 1, 1, Effect);
        var ecm = Assert.IsType<ElectronicWarfareActivation>(first.ElectronicWarfare);
        Assert.Equal(range, ecm.Module.Optimal);
        Assert.Equal(range, ecm.Module.Falloff);
        Assert.Equal(strength * 2, ecm.Module.Power.Jamming!.Gravimetric);
        Assert.Equal(strength / 2, ecm.Module.Power.Jamming.Radar);
        Assert.Equal(capacity - cost, ecm.Capacitor);
        Assert.Equal(TimeSpan.FromSeconds(20).Ticks, first.Cycle.ReadyAt - first.Cycle.StartedAt);
        Assert.Null(weapons.Activate(lease.Token, module, Npc, 1, 1, Effect).ElectronicWarfare);
        var live = LiveWorld.Find(f.Db.Path)!;
        var timedModules = new LiveTimedModules(live, Fixture.SystemId);
        var effects = f.Db.Services.GetRequiredService<IPilotElectronicWarfare>();
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(20) - TimeSpan.FromTicks(1));
        timedModules.Expire(f.Clock.Now);
        Assert.Single(effects.Incoming(Npc));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        timedModules.Expire(f.Clock.Now);
        Assert.Empty(effects.Incoming(Npc));
        var repeat = Assert.Single(weapons.Advance(lease.Token, 2).Started);
        Assert.Equal(0, repeat.Cycle.Repeats);
        Assert.Equal(repeat.Cycle.StartedAt, Assert.Single(effects.Incoming(Npc)).StartedAt);
        Assert.Equal(1, Assert.Single(ammunition.Read(f.PilotId, structure.Id)).Quantity);
        lease.Dispose();
        Assert.Empty(effects.Incoming(Npc));
        using var action = StructureAttackTests.Scope(f);
        Assert.Equal(repeat.Cycle.ReadyAt, action.World.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(repeat.ElectronicWarfare!.Capacitor, action.World.Set<SavedStructureEnergy>().Find(structure.Id)!.Charge);
    }

    [Fact]
    public void NativeEcmNotifiesVictimAndAllowsLockingOnlyTheJammer()
    {
        using var f = StructureAttackTests.Prepare();
        var other = f.Db.Store.FindPilot(1000002)!;
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            int type = other.ShipTypeId;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({type},{(int)DogmaAttributeId.RadarStrength},10)");
        }
        var (structure, module) = Prepare(f, 35940);
        f.Db.Store.SetPosition(
            other.ShipId,
            structure.Position + new SpacePosition(structure.Profile.Radius + 1000, 0, 0),
            Fixture.SystemId
        );
        AddNpc(f, structure);
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
        var receiver = peerScope.ServiceProvider.GetRequiredService<PlayerEwarHandler>();
        receiver.Advance(peer);
        var jam = Assert.Single(peer.NotificationsBeforeFlight.OfType<PlayerJamChanged>());
        Assert.True(jam.Active);
        Assert.Equal(new object?[] { structure.Id, module, other.ShipId, "electronic", f.Clock.Now, 20000d }, jam.Arguments);
        var targeting = peerScope.ServiceProvider.GetRequiredService<TargetingHandler>();
        Assert.IsType<PilotCallResult.Failure>(targeting.Call(peer, "dogmaIM", "AddTarget", [Npc]));
        Assert.IsType<PilotCallResult.Success>(targeting.Call(peer, "dogmaIM", "AddTarget", [structure.Id]));
        gunner.ReleaseStructureControl();
        receiver.Advance(peer);
        Assert.Contains(peer.NotificationsBeforeFlight.OfType<PlayerJamChanged>(), n => !n.Active);
    }

    [Fact]
    public void EcmFalloffUsesRacialStrengthAndResistanceBeforeClampingChance()
    {
        var module = PlayerEwarRules.Define(
            1,
            35940,
            Effect,
            new Dictionary<DogmaAttributeId, double>
            {
                [DogmaAttributeId.Duration] = 20000,
                [DogmaAttributeId.MaxRange] = 75000,
                [DogmaAttributeId.FalloffEffectiveness] = 75000,
                [DogmaAttributeId.GravimetricJammingStrength] = 30,
                [DogmaAttributeId.RadarJammingStrength] = 60,
            }
        );
        Dictionary<DogmaAttributeId, double> target = new()
        {
            [DogmaAttributeId.GravimetricStrength] = 20,
            [DogmaAttributeId.RadarStrength] = 5,
        };
        Assert.NotNull(StructureWeaponRules.Jam(module, target, 150000, 0.74999));
        Assert.Null(StructureWeaponRules.Jam(module, target, 150000, 0.75));
        target[DogmaAttributeId.EcmResistance] = 0.5;
        Assert.NotNull(StructureWeaponRules.Jam(module, target, 150000, 0.37499));
        Assert.Null(StructureWeaponRules.Jam(module, target, 150000, 0.375));
        target[DogmaAttributeId.EcmResistance] = 0;
        Assert.Null(StructureWeaponRules.Jam(module, target, 75000, 0));
        Assert.Throws<InvalidDataException>(() => StructureWeaponRules.Jam(module, target, 150001, 0));
    }
}
