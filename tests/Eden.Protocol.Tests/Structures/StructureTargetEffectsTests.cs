using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Structures.StructureDefenseFixture;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureTargetEffectsTests
{
    [Theory]
    [InlineData(35947, DogmaEffect.StructureTargetPainter, 75_000, 150_000, 1000, 10_000, 65)]
    [InlineData(47366, DogmaEffect.StructureTargetPainter, 90_000, 180_000, 1200, 10_000, 72)]
    [InlineData(35941, DogmaEffect.StructureSensorDampener, 75_000, 150_000, 400, 5_000, -55)]
    [InlineData(47368, DogmaEffect.StructureSensorDampener, 90_000, 180_000, 480, 5_000, -61)]
    public void StandupPainterAndDampenerUsePinnedRangeCostAndFalloff(
        int type,
        DogmaEffect effect,
        double optimal,
        double falloff,
        double cost,
        double duration,
        double strength
    )
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, type);
        AddNpc(f, structure);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        Lock(f, lease.Token, Npc);
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        double capacity = structure.Fitting!.Attributes[DogmaAttributeId.CapacitorCapacity];
        using (var boundary = StructureAttackTests.Scope(f))
        {
            var npc = boundary.World.Set<WorldNpc>().Find(Npc)!;
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
            npc.X = structure.Position.X + structure.Profile.Radius + (catalog.RequireItem(npc.TypeId).Radius ?? 0) + optimal + falloff + 1;
            boundary.Commit();
        }
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, module, Npc, 2, 1, effect));
        using (var boundary = StructureAttackTests.Scope(f))
        {
            Assert.Null(boundary.World.Set<SavedStructureEnergy>().Find(structure.Id));
            Assert.Null(boundary.World.Set<ModuleCooldown>().Find(module));
            boundary.World.Set<WorldNpc>().Find(Npc)!.X -= 1;
            boundary.Commit();
        }
        var activation = weapons.Activate(lease.Token, module, Npc, 2, 1, effect);
        var ewar = Assert.IsType<ElectronicWarfareActivation>(activation.ElectronicWarfare);
        Assert.Equal(optimal, ewar.Module.Optimal);
        Assert.Equal(falloff, ewar.Module.Falloff);
        Assert.Equal(capacity - cost, ewar.Capacitor);
        Assert.Equal(duration, activation.Cycle.DurationMilliseconds);
        Assert.Equal(2, activation.Cycle.Repeats);
        Assert.Null(weapons.Activate(lease.Token, module, Npc, 2, 1, effect).ElectronicWarfare);

        bool painting = effect == DogmaEffect.StructureTargetPainter;
        using (var action = StructureAttackTests.Scope(f))
        {
            Dictionary<DogmaAttributeId, double> before = new()
            {
                [DogmaAttributeId.SignatureRadius] = 400,
                [DogmaAttributeId.MaxTargetRange] = 100_000,
                [DogmaAttributeId.ScanResolution] = 400,
                [DogmaAttributeId.TargetPainterResistance] = .5,
                [DogmaAttributeId.SensorDampenerResistance] = .5,
            };
            var changed = PlayerEwarStore.NpcAttributes(action.World, Npc, before);
            Assert.Equal(400 * (painting ? 1 + strength / 400 : 1), changed[DogmaAttributeId.SignatureRadius], 6);
            Assert.Equal(100_000 * (painting ? 1 : 1 + strength / 400), changed[DogmaAttributeId.MaxTargetRange], 6);
            Assert.Equal(400 * (painting ? 1 : 1 + strength / 400), changed[DogmaAttributeId.ScanResolution], 6);
        }
        weapons.Deactivate(lease.Token, module, effect);
        var effects = f.Db.Services.GetRequiredService<IPilotElectronicWarfare>();
        var timedModules = new LiveTimedModules(LiveWorld.Find(f.Db.Path)!, Fixture.SystemId);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(duration) - TimeSpan.FromTicks(1));
        timedModules.Expire(f.Clock.Now);
        Assert.Single(effects.Incoming(Npc));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        timedModules.Expire(f.Clock.Now);
        Assert.Empty(effects.Incoming(Npc));
        var expired = weapons.Advance(lease.Token, 2);
        Assert.Empty(expired.Started);
        Assert.Single(expired.Stopped);
    }

    [Theory]
    [InlineData(35947, "structureModuleEffectTargetPainter", DogmaEffect.StructureTargetPainter, "ewTargetPaint")]
    [InlineData(35941, "structureModuleEffectRemoteSensorDampener", DogmaEffect.StructureSensorDampener, "ewRemoteSensorDamp")]
    public void StandupPainterAndDampenerPublishNativeVictimEffectsAndClearOnRelease(int type, string name, DogmaEffect effect, string slot)
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, type);
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
        object?[] activate = [module, name, other.ShipId, 0L];
        Assert.IsType<PilotCallResult.Failure>(Call("dogmaIM", "Activate", activate));
        using (var retaliation = StructureAttackTests.Scope(f))
        {
            StructureCombatTargets.Record(retaliation.World, other.ShipId, structure.Id, f.Clock.Now, f.Clock.Now);
            retaliation.Commit();
        }
        var fitting = peerScope.ServiceProvider.GetRequiredService<IPilotFitting>();
        var attribute = effect == DogmaEffect.StructureTargetPainter ? DogmaAttributeId.SignatureRadius : DogmaAttributeId.MaxTargetRange;
        double before = fitting.ShipAttributes(other.PilotId)[attribute];
        Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "Activate", activate));
        var dogma = Assert.IsType<DogmaAllInfo>(
            Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "GetAllInfo", [true, true, true])).Reply
        );
        Assert.Contains(effect, dogma.ShipInfo[module].ActiveEffects.Keys);
        var receiver = peerScope.ServiceProvider.GetRequiredService<PlayerEwarHandler>();
        receiver.Advance(peer);
        var started = Assert.Single(peer.NotificationsBeforeFlight.OfType<PlayerEwarChanged>());
        Assert.True(started.Active);
        Assert.Equal(new object?[] { structure.Id, module, other.ShipId, slot }, started.Arguments);
        double factor = effect == DogmaEffect.StructureTargetPainter ? 1.65 : .45;
        Assert.Equal(before * factor, fitting.ShipAttributes(other.PilotId)[attribute], 6);
        gunner.ReleaseStructureControl();
        receiver.Advance(peer);
        Assert.Contains(peer.NotificationsBeforeFlight.OfType<PlayerEwarChanged>(), notification => !notification.Active);
        Assert.Equal(before, fitting.ShipAttributes(other.PilotId)[attribute], 6);
    }
}
