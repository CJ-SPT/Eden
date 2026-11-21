using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Skills;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Structures;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Structures;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Structures.StructureDefenseFixture;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureNeutralizerTests
{
    private const int Heavy = 35925;
    private const DogmaEffect Effect = DogmaEffect.StructureEnergyNeutralizer;

    [Theory]
    [InlineData(Heavy, 1000, 1500, 15000)]
    [InlineData(47332, 1000, 1800, 15000)]
    [InlineData(46575, 1000, 1900, 15000)]
    [InlineData(35924, 6000, 10000, 30000)]
    [InlineData(47330, 6000, 12000, 30000)]
    public void PublishedNeutralizersChargeTheStructureAndDrainNpcEnergy(int type, double cost, double drain, double duration)
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, type);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        AddNpc(f, structure);
        Lock(f, lease.Token, Npc);
        using (var skill = StructureAttackTests.Scope(f))
        {
            skill.World.Add(
                new PilotSkill
                {
                    PilotId = f.PilotId,
                    TypeId = StructureWeaponRules.EngineeringSystemsSkill,
                    Points = 256000000,
                }
            );
            skill.Commit();
        }
        double capacity = structure.Fitting!.Attributes[DogmaAttributeId.CapacitorCapacity];
        var resources = f.Db.Services.GetRequiredService<IPilotShipResources>();
        double shipCharge = resources.Capacitor(f.PilotId, f.Clock.Now);
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, module, Npc, 1, 1));
        var activation = weapons.Activate(lease.Token, module, Npc, 1, 1, Effect);
        var hit = Assert.IsType<CapacitorActivation>(activation.Neutralization);
        Assert.Equal(duration, hit.Cycle.Module.DurationMilliseconds);
        Assert.Equal(cost * 0.9, hit.Cycle.Module.CapacitorNeed, 6);
        Assert.Equal(capacity - cost * 0.9, hit.Capacitor, 6);
        double expected = duration == 30000 ? drain * Signature / 15000 : drain;
        Assert.Equal(expected, hit.Applied, 6);
        Assert.Equal(NpcCapacity - expected, hit.TargetCapacitor, 6);
        Assert.Equal(shipCharge, resources.Capacitor(f.PilotId, f.Clock.Now));
        Assert.Null(weapons.Activate(lease.Token, module, Npc, 1, 1, Effect).Neutralization);
        using var current = StructureAttackTests.Scope(f);
        Assert.Equal(hit.Capacitor, current.World.Set<SavedStructureEnergy>().Find(structure.Id)!.Charge);
        Assert.Equal(hit.TargetCapacitor, current.World.Set<WorldNpcEnergy>().Find(Npc)!.Charge);
        Assert.Empty(current.World.Set<WorldMissile>());
        Assert.Equal(activation.Cycle.ReadyAt, current.World.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public async Task RechargeAndCooldownPersistAcrossControlReleaseAndReopen()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, Heavy);
        var control = f.Db.Services.GetRequiredService<IPilotStructureControl>();
        using var lease = control.Acquire(structure.Id, f.PilotId, 101);
        AddNpc(f, structure);
        Lock(f, lease.Token, Npc);
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        using (var low = StructureAttackTests.Scope(f))
        {
            StructureEnergy.Write(low.World, structure.Id, 999, f.Clock.Now);
            low.Commit();
        }
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, module, Npc, 2, 1, Effect));
        using (var reset = StructureAttackTests.Scope(f))
        {
            Assert.Equal(999, reset.World.Set<SavedStructureEnergy>().Find(structure.Id)!.Charge);
            Assert.Null(reset.World.Set<WorldNpcEnergy>().Find(Npc));
            Assert.Null(reset.World.Set<ModuleCooldown>().Find(module));
            StructureEnergy.Write(reset.World, structure.Id, 5000, f.Clock.Now);
            reset.Commit();
        }
        var paid = weapons.Activate(lease.Token, module, Npc, 2, 1, Effect);
        Assert.Equal(4000, paid.Neutralization!.Capacitor);
        lease.Dispose();
        using var replacement = control.Acquire(structure.Id, f.PilotId, 102);
        Assert.Empty(weapons.Read(replacement.Token));
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-capacitor-reopen.sqlite");
        File.Copy(f.Db.Path, copy);
        var clock = new TestClock(f.Clock.UtcNow);
        using var reopened = ServerServices.Build(new(copy), clock, population: new TestNpcPopulationSeeder(), liveWorld: true);
        var structures = reopened.GetRequiredService<IPilotStructures>();
        var snapshot = structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(4000, snapshot.Capacitor);
        Assert.Empty(snapshot.Weapons);
        Assert.Equal(paid.Cycle.ReadyAt, Assert.Single(snapshot.Equipment).ReadyAt);
        clock.AdvanceWallTime(TimeSpan.FromMilliseconds(500));
        var fit = snapshot.Fitting!.Attributes;
        double expected = ChargedResource.Recharged(
            4000,
            fit[DogmaAttributeId.CapacitorCapacity],
            fit[DogmaAttributeId.CapacitorRechargeTime],
            500
        );
        Assert.Equal(expected, structures.Read(f.PilotId, structure.Id)!.Capacitor!.Value, 8);
        using WorldDbContext saved = new(copy);
        Assert.Equal(NpcCapacity - 1500, saved.Set<WorldNpcEnergy>().Find(Npc)!.Charge);
    }

    [Fact]
    public void RefittingAStandupCapBatteryCarriesForwardTheRechargedFraction()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, Heavy);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        AddNpc(f, structure);
        Lock(f, lease.Token, Npc);
        var paid = f.Db.Services.GetRequiredService<IPilotStructureWeapons>().Activate(lease.Token, module, Npc, 0, 1, Effect);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(500));
        var before = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.True(before.Capacitor > paid.Neutralization!.Capacitor);
        double fraction = before.Capacitor!.Value / before.Fitting!.Attributes[DogmaAttributeId.CapacitorCapacity];
        const int standupCapBattery = 47352;
        long battery = f.Item(standupCapBattery);
        var fitting = f.Db.Services.GetRequiredService<IPilotStructureFitting>();
        var expanded = fitting.Fit(f.PilotId, structure.Id, [new(battery)]).Structure;
        double capacity = expanded.Fitting!.Attributes[DogmaAttributeId.CapacitorCapacity];
        Assert.True(capacity > before.Fitting.Attributes[DogmaAttributeId.CapacitorCapacity]);
        Assert.Equal(fraction * capacity, expanded.Capacitor!.Value, 7);
        var reduced = fitting.SetOnline(f.PilotId, structure.Id, battery, false).Structure;
        Assert.Equal(before.Capacitor.Value, reduced.Capacitor!.Value, 7);
    }

    [Fact]
    public void NativeNeutralizerUsesGreenSafetyAndPublishesBothCapacitors()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, Heavy);
        var other = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(
            other.ShipId,
            structure.Position + new SpacePosition(structure.Profile.Radius + 1000, 0, 0),
            Fixture.SystemId
        );
        using var peerScope = f.Db.Services.CreateScope();
        using var peer = Session(f, peerScope, 1000002, 102);
        peer.Flight.Snapshot();
        var receiver = peerScope.ServiceProvider.GetRequiredService<CapacitorWarfareHandler>();
        receiver.Enter(peer);
        using var scope = f.Db.Services.CreateScope();
        using var context = Session(f, scope, 1000001, 101);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(string service, string method, object?[] args) => router.Call(context, service, method, args, _ => { });
        Assert.IsType<PilotCallResult.Success>(Call("structureControl", "TakeControl", [structure.Id]));
        Lock(f, context.StructureControl!.Token, other.ShipId);
        const string effectName = "structureEnergyNeutralizerFalloff";
        object?[] activate = [module, effectName, other.ShipId, 2L];
        Assert.IsType<PilotCallResult.Failure>(Call("dogmaIM", "Activate", activate));
        using (var legal = StructureAttackTests.Scope(f))
        {
            Assert.Null(legal.World.Set<SavedStructureEnergy>().Find(structure.Id));
            legal.World.Add(
                new PilotSkill
                {
                    PilotId = f.PilotId,
                    TypeId = StructureWeaponRules.EngineeringSystemsSkill,
                    Points = 256000000,
                }
            );
            StructureCombatTargets.Record(legal.World, other.ShipId, structure.Id, f.Clock.Now, f.Clock.Now);
            legal.Commit();
        }
        Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "Activate", activate));
        var active = Assert.Single(context.Notifications.OfType<ShipEffectChanged>(), e => e.Effect.Effect == Effect && e.Active);
        Assert.Equal(structure.Id, active.Effect.ShipId);
        Assert.Equal(other.ShipId, active.Effect.TargetId);
        var info = Assert.IsType<DogmaAllInfo>(
            Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "GetAllInfo", [true, true, true])).Reply
        );
        Assert.Contains(Effect, info.ShipInfo[module].ActiveEffects.Keys);
        Assert.Equal(900, info.ShipInfo[module].Attributes[DogmaAttributeId.CapacitorNeed]);
        double charge = structure.Fitting!.Attributes[DogmaAttributeId.CapacitorCapacity] - 900;
        Assert.Equal(charge, info.ShipInfo[structure.Id].Attributes[DogmaAttributeId.CapacitorCharge]);
        Assert.Equal(charge, info.ShipState.Instances[structure.Id].Capacitor);
        peer.Flight.RefreshStructures(f.Db.Services.GetRequiredService<IStructureContacts>().Contacts(Fixture.SystemId));
        var observer = peerScope.ServiceProvider.GetRequiredService<StructureWeaponHandler>();
        observer.Advance(peer);
        observer.Advance(peer);
        Assert.Single(
            peer.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.TimedSpecialEffect>(),
            e => e.Id == structure.Id && e.TargetId == other.ShipId && e.Graphic == "effects.Laser" && e.Start
        );
        receiver.Advance(peer);
        Assert.Contains(
            peer.NotificationsBeforeFlight.OfType<CapacitorEwarChanged>(),
            e => e.Active && e.Cycle.SourceShipId == structure.Id
        );
        Assert.Contains(
            peer.Notifications.OfType<ModuleAttributesChanged>().SelectMany(n => n.Changes),
            change =>
                change.Attribute == DogmaAttributeId.CapacitorCharge
                && change.Item == new DogmaItemKey.Item(other.ShipId)
                && Equals(change.Value, 0d)
        );
        var weapons = scope.ServiceProvider.GetRequiredService<StructureWeaponHandler>();
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        weapons.Advance(context);
        var next = f.Structures.Read(f.PilotId, structure.Id)!;
        Assert.Equal(1, Assert.Single(next.Weapons).Repeats);
        weapons.Advance(context);
        Assert.Equal(next.Capacitor, f.Structures.Read(f.PilotId, structure.Id)!.Capacitor);
        Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "Deactivate", [module, effectName]));
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(15));
        weapons.Advance(context);
        receiver.Advance(peer);
        observer.Advance(peer);
        Assert.Contains(
            peer.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.TimedSpecialEffect>(),
            e => e.Id == structure.Id && e.ModuleId == module && !e.Start
        );
        Assert.Empty(f.Structures.Read(f.PilotId, structure.Id)!.Weapons);
        Assert.Contains(peer.NotificationsBeforeFlight.OfType<CapacitorEwarChanged>(), e => !e.Active);
    }
}
