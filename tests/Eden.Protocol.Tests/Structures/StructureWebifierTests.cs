using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Skills;
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
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Structures.StructureDefenseFixture;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureWebifierTests
{
    private const DogmaEffect Effect = DogmaEffect.StructureStasisWebifier;
    private const string NativeEffect = "structureModuleEffectStasisWebifier";

    [Theory]
    [InlineData(35943, 3000, -70, 200000)]
    [InlineData(47351, 3600, -75, 300000)]
    public async Task WebSlowsNpcForPaidCycleAndCooldownSurvivesRelease(int type, double cost, double percent, double range)
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, type);
        var control = f.Db.Services.GetRequiredService<IPilotStructureControl>();
        using var lease = control.Acquire(structure.Id, f.PilotId, 101);
        AddNpc(f, structure);
        Lock(f, lease.Token, Npc);
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        double capacity = structure.Fitting!.Attributes[DogmaAttributeId.CapacitorCapacity];
        using (var boundary = StructureAttackTests.Scope(f))
        {
            var npc = boundary.World.Set<WorldNpc>().Find(Npc)!;
            using Eden.World.Catalog.CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
            npc.X = structure.Position.X + structure.Profile.Radius + (catalog.RequireItem(npc.TypeId).Radius ?? 0) + range + 1;
            boundary.Commit();
        }
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, module, Npc, 2, 1, Effect));
        using (var boundary = StructureAttackTests.Scope(f))
        {
            Assert.Null(boundary.World.Set<SavedStructureEnergy>().Find(structure.Id));
            boundary.World.Set<WorldNpc>().Find(Npc)!.X -= 1;
            boundary.Commit();
        }
        using (var low = StructureAttackTests.Scope(f))
        {
            StructureEnergy.Write(low.World, structure.Id, cost - 1, f.Clock.Now);
            low.Commit();
        }
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, module, Npc, 2, 1, Effect));
        using (var reset = StructureAttackTests.Scope(f))
        {
            Assert.Null(reset.World.Set<WorldTackle>().Find(module));
            Assert.Null(reset.World.Set<ModuleCooldown>().Find(module));
            Assert.Equal(cost - 1, reset.World.Set<SavedStructureEnergy>().Find(structure.Id)!.Charge);
            StructureEnergy.Write(reset.World, structure.Id, capacity, f.Clock.Now);
            reset.Commit();
        }
        var first = weapons.Activate(lease.Token, module, Npc, 2, 1, Effect);
        Assert.Equal(0, first.Cycle.Repeats);
        Assert.Equal(capacity - cost, first.Tackle!.Capacitor);
        Assert.Null(weapons.Activate(lease.Token, module, Npc, 2, 1, Effect).Tackle);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, first.Cycle.ReadyAt - first.Cycle.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, first.Cycle.ReactivationAt - first.Cycle.ReadyAt);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var web = saved.Set<WorldTackle>().Single();
            Assert.Null(web.SourceShipId);
            Assert.Equal(structure.Id, web.SourceStructureId);
            Assert.Equal(range, web.Range);
            Assert.Equal(percent, web.WebPercent);
            Assert.Equal(first.Cycle.ReactivationAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        }
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "web-checkpoint.sqlite");
        File.Copy(f.Db.Path, copy);
        WorldInitialization.Initialize(new(copy), clock: f.Clock);
        using (WorldDbContext reopened = new(copy))
        {
            Assert.Empty(reopened.Set<WorldTackle>());
            Assert.Equal(first.Cycle.ReactivationAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
            Assert.Equal(capacity - cost, reopened.Set<SavedStructureEnergy>().Find(structure.Id)!.Charge);
        }
        using (var read = StructureAttackTests.Scope(f))
        {
            var attrs = PilotTackleStore.NpcAttributes(
                read.World,
                Npc,
                new Dictionary<DogmaAttributeId, double> { [DogmaAttributeId.MaxVelocity] = 100, [DogmaAttributeId.EntityCruiseSpeed] = 50 }
            );
            Assert.Equal(100 + percent, attrs[DogmaAttributeId.MaxVelocity], 7);
            Assert.Equal((100 + percent) / 2, attrs[DogmaAttributeId.EntityCruiseSpeed], 7);
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        var ended = weapons.Advance(lease.Token, 1);
        Assert.Single(ended.Stopped);
        Assert.Empty(ended.Started);
        Assert.Empty(f.Structures.Read(f.PilotId, structure.Id)!.Weapons);
        using (var read = StructureAttackTests.Scope(f))
        {
            Assert.Null(read.World.Set<WorldTackle>().Find(module));
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(59));
        Assert.Empty(weapons.Advance(lease.Token, 1).Started);
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, module, Npc, 0, 1, Effect));
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Assert.Empty(weapons.Advance(lease.Token, 1).Started);
        var next = weapons.Activate(lease.Token, module, Npc, 0, 1, Effect);
        lease.Dispose();
        using var replacement = control.Acquire(structure.Id, f.PilotId, 102);
        Lock(f, replacement.Token, Npc);
        Assert.Throws<InvalidDataException>(() => weapons.Activate(replacement.Token, module, Npc, 0, 1, Effect));
        using var after = StructureAttackTests.Scope(f);
        Assert.Null(after.World.Set<WorldTackle>().Find(module));
        Assert.Equal(next.Cycle.ReactivationAt, after.World.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void NativeWebShowsGunnerAndVictimThePaidEffectAndRestoresSpeedAtExpiry()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, 35943);
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
        using var context = Session(f, scope, 1000001, 101);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(string service, string method, object?[] args) => router.Call(context, service, method, args, _ => { });
        Assert.IsType<PilotCallResult.Success>(Call("structureControl", "TakeControl", [structure.Id]));
        Lock(f, context.StructureControl!.Token, other.ShipId);
        object?[] args = [module, NativeEffect, other.ShipId, 0L];
        Assert.IsType<PilotCallResult.Failure>(Call("dogmaIM", "Activate", args));
        using (var legal = StructureAttackTests.Scope(f))
        {
            Assert.Null(legal.World.Set<SavedStructureEnergy>().Find(structure.Id));
            legal.World.Add(
                new PilotSkill
                {
                    PilotId = f.PilotId,
                    TypeId = StructureWeaponRules.ElectronicSystemsSkill,
                    Points = 256000000,
                }
            );
            StructureCombatTargets.Record(legal.World, other.ShipId, structure.Id, f.Clock.Now, f.Clock.Now);
            legal.Commit();
        }
        var tackle = peerScope.ServiceProvider.GetRequiredService<IPilotTackle>();
        double speed = tackle.ShipAttributes(other.PilotId)[DogmaAttributeId.MaxVelocity];
        Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "Activate", args));
        var info = Assert.IsType<DogmaAllInfo>(
            Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "GetAllInfo", [true, true, true])).Reply
        );
        Assert.Contains(Effect, info.ShipInfo[module].ActiveEffects.Keys);
        Assert.Equal(2550, info.ShipInfo[module].Attributes[DogmaAttributeId.CapacitorNeed]);
        Assert.Equal(f.Clock.Now + TimeSpan.FromSeconds(90).Ticks, info.ShipInfo[module].Attributes[DogmaAttributeId.NextActivationTime]);
        peer.Flight.RefreshStructures(f.Db.Services.GetRequiredService<IStructureContacts>().Contacts(Fixture.SystemId));
        var receiver = peerScope.ServiceProvider.GetRequiredService<TackleHandler>();
        var observer = peerScope.ServiceProvider.GetRequiredService<StructureWeaponHandler>();
        receiver.Advance(peer);
        observer.Advance(peer);
        observer.Advance(peer);
        Assert.Equal(speed * .3, tackle.ShipAttributes(other.PilotId)[DogmaAttributeId.MaxVelocity], 5);
        var incoming = Assert.Single(peer.Notifications.OfType<TackleEwarChanged>(), e => e.Active);
        Assert.Equal(new object?[] { structure.Id, module, other.ShipId, "webify" }, incoming.Arguments);
        Assert.Single(
            peer.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.TimedSpecialEffect>(),
            e => e.Id == structure.Id && e.TargetId == other.ShipId && e.Graphic == "effects.Laser" && e.Start
        );
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        // Only the victim advances. A late gunner cannot extend paid movement modifiers.
        receiver.Advance(peer);
        observer.Advance(peer);
        Assert.Equal(speed, tackle.ShipAttributes(other.PilotId)[DogmaAttributeId.MaxVelocity]);
        Assert.Contains(peer.Notifications.OfType<TackleEwarChanged>(), e => !e.Active);
        Assert.Contains(
            peer.Flight.Updates.Select(u => u.Command).OfType<DestinyCommand.TimedSpecialEffect>(),
            e => e.Id == structure.Id && e.ModuleId == module && !e.Start
        );
        Assert.IsType<PilotCallResult.Failure>(Call("dogmaIM", "Activate", args));
    }
}
