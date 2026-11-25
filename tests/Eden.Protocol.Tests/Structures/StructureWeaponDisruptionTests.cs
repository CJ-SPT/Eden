using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
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
using Eden.World.Inventory;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Structures.StructureDefenseFixture;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureWeaponDisruptionTests
{
    private const DogmaEffect Effect = DogmaEffect.StructureWeaponDisruption;
    private const int Blaster = 561;
    private const int LightLauncher = 499;
    private const int LightMissile = 212;

    [Theory]
    [InlineData(35945, 150_000, 75_000, 400, 60, 37, 30, 50)]
    [InlineData(47364, 180_000, 90_000, 480, 66, 41, 33, 55)]
    public void StandupDisruptorsUsePinnedTurretAndMissileStrengthAtFalloff(
        int type,
        double optimal,
        double falloff,
        double cost,
        double tracking,
        double velocity,
        double lifetime,
        double explosion
    )
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, module) = Prepare(f, type);
        AddNpc(f, structure);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        Lock(f, lease.Token, Npc);
        using (var boundary = StructureAttackTests.Scope(f))
        {
            var npc = boundary.World.Set<WorldNpc>().Find(Npc)!;
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
            npc.X = structure.Position.X + structure.Profile.Radius + (catalog.RequireItem(npc.TypeId).Radius ?? 0) + optimal + falloff;
            boundary.Commit();
        }
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        var paid = weapons.Activate(lease.Token, module, Npc, 0, 1, Effect);
        var ewar = Assert.IsType<ElectronicWarfareActivation>(paid.ElectronicWarfare);
        Assert.Equal(optimal, ewar.Module.Optimal);
        Assert.Equal(falloff, ewar.Module.Falloff);
        Assert.Equal(structure.Fitting!.Attributes[DogmaAttributeId.CapacitorCapacity] - cost, ewar.Capacitor);
        Assert.Equal(5000, paid.Cycle.DurationMilliseconds);
        var contact = Assert.Single(ewar.Contacts);
        Assert.Equal(-tracking / 2, contact.Applied!.TrackingPercent);
        Assert.Equal(-velocity / 2, contact.Applied.MissileSpeedPercent);
        using (var action = StructureAttackTests.Scope(f))
        {
            Dictionary<DogmaAttributeId, double> before = new()
            {
                [DogmaAttributeId.MaxRange] = 10_000,
                [DogmaAttributeId.AccuracyFalloff] = 10_000,
                [DogmaAttributeId.TrackingSpeed] = 2,
                [DogmaAttributeId.WeaponDisruptionResistance] = .5,
            };
            var changed = PlayerEwarStore.NpcAttributes(action.World, Npc, before);
            Assert.Equal(10_000 * (1 - tracking / 400), changed[DogmaAttributeId.MaxRange], 8);
            Assert.Equal(10_000 * (1 - tracking / 400), changed[DogmaAttributeId.AccuracyFalloff], 8);
            Assert.Equal(2 * (1 - tracking / 400), changed[DogmaAttributeId.TrackingSpeed], 8);
            Assert.Equal(1 - velocity / 400, changed[DogmaAttributeId.EntityMissileVelocityMultiplier], 8);
            Assert.Equal(1 - lifetime / 400, changed[DogmaAttributeId.EntityMissileFlightTimeMultiplier], 8);
            Assert.Equal(1 + explosion / 400, changed[DogmaAttributeId.EntityMissileExplosionRadiusMultiplier], 8);
            Assert.Equal(1 - explosion / 400, changed[DogmaAttributeId.EntityMissileExplosionVelocityMultiplier], 8);
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(5));
        Assert.Single(weapons.Advance(lease.Token, 2).Stopped);
        Assert.Empty(f.Db.Services.GetRequiredService<IPilotElectronicWarfare>().Incoming(Npc));
    }

    [Fact]
    public void NativeStandupDisruptorChangesFittedTurretAndLoadedMissileTogether()
    {
        using var f = StructureAttackTests.Prepare();
        PassiveCatalogFixture.LoadMissileTiers(new WorldDatabase(f.Db.Path).CatalogPath, new HashSet<int> { LightLauncher, LightMissile });
        var (structure, module) = Prepare(f, 35945);
        var other = f.Db.Store.FindPilot(1000002)!;
        long otherShip = other.ShipId;
        (long turret, long launcher) = InstallVictimWeapons(f, other.PilotId, otherShip);
        f.Db.Store.SetPosition(otherShip, structure.Position + new SpacePosition(structure.Profile.Radius + 1000, 0, 0), Fixture.SystemId);
        using var peerScope = f.Db.Services.CreateScope();
        using var peer = Session(f, peerScope, 1000002, 102);
        peer.Flight.Snapshot();
        using var scope = f.Db.Services.CreateScope();
        using var gunner = Session(f, scope, 1000001, 101);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        PilotCallResult Call(string service, string method, object?[] args) => router.Call(gunner, service, method, args, _ => { });
        Assert.IsType<PilotCallResult.Success>(Call("structureControl", "TakeControl", [structure.Id]));
        Lock(f, gunner.StructureControl!.Token, otherShip);
        object?[] activate = [module, "structureModuleEffectWeaponDisruption", otherShip, 0L];
        Assert.IsType<PilotCallResult.Failure>(Call("dogmaIM", "Activate", activate));
        using (var retaliation = StructureAttackTests.Scope(f))
        {
            StructureCombatTargets.Record(retaliation.World, otherShip, structure.Id, f.Clock.Now, f.Clock.Now);
            retaliation.Commit();
        }
        var fitting = peerScope.ServiceProvider.GetRequiredService<IPilotFitting>();
        var inventory = peerScope.ServiceProvider.GetRequiredService<IPilotInventory>();
        var ammo = peerScope.ServiceProvider.GetRequiredService<IPilotAmmunition>();
        var gun = Assert.Single(inventory.List(other.PilotId, otherShip), item => item.Id == turret);
        var initialTurret = fitting.ModuleAttributes(other.PilotId, gun);
        var initialMissile = ammo.ChargeAttributes(other.PilotId, otherShip, launcher);
        Assert.All(
            new[] { DogmaAttributeId.MaxRange, DogmaAttributeId.AccuracyFalloff, DogmaAttributeId.TrackingSpeed },
            attribute => Assert.True(initialTurret[attribute] > 0)
        );
        Assert.All(
            new[]
            {
                DogmaAttributeId.MaxVelocity,
                DogmaAttributeId.MissileFlightTime,
                DogmaAttributeId.ExplosionVelocity,
                DogmaAttributeId.ExplosionRadius,
            },
            attribute => Assert.True(initialMissile[attribute] > 0)
        );
        Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "Activate", activate));
        var activeTurret = fitting.ModuleAttributes(other.PilotId, gun);
        var activeMissile = ammo.ChargeAttributes(other.PilotId, otherShip, launcher);
        Assert.Equal(initialTurret[DogmaAttributeId.MaxRange] * .4, activeTurret[DogmaAttributeId.MaxRange], 6);
        Assert.Equal(initialTurret[DogmaAttributeId.AccuracyFalloff] * .4, activeTurret[DogmaAttributeId.AccuracyFalloff], 6);
        Assert.Equal(initialTurret[DogmaAttributeId.TrackingSpeed] * .4, activeTurret[DogmaAttributeId.TrackingSpeed], 6);
        Assert.Equal(initialMissile[DogmaAttributeId.MaxVelocity] * .63, activeMissile[DogmaAttributeId.MaxVelocity], 6);
        Assert.Equal(initialMissile[DogmaAttributeId.MissileFlightTime] * .7, activeMissile[DogmaAttributeId.MissileFlightTime], 6);
        Assert.Equal(initialMissile[DogmaAttributeId.ExplosionVelocity] * .5, activeMissile[DogmaAttributeId.ExplosionVelocity], 6);
        Assert.Equal(initialMissile[DogmaAttributeId.ExplosionRadius] * 1.5, activeMissile[DogmaAttributeId.ExplosionRadius], 6);
        var receiver = peerScope.ServiceProvider.GetRequiredService<PlayerEwarHandler>();
        receiver.Advance(peer);
        var effect = Assert.Single(peer.NotificationsBeforeFlight.OfType<PlayerEwarChanged>());
        Assert.True(effect.Active);
        Assert.Equal(new object?[] { structure.Id, module, otherShip, "ewTrackingDisrupt" }, effect.Arguments);
        gunner.ReleaseStructureControl();
        receiver.Advance(peer);
        Assert.Contains(peer.NotificationsBeforeFlight.OfType<PlayerEwarChanged>(), notification => !notification.Active);
        Assert.Equal(
            initialTurret[DogmaAttributeId.TrackingSpeed],
            fitting.ModuleAttributes(other.PilotId, gun)[DogmaAttributeId.TrackingSpeed],
            6
        );
        Assert.Equal(
            initialMissile[DogmaAttributeId.MaxVelocity],
            ammo.ChargeAttributes(other.PilotId, otherShip, launcher)[DogmaAttributeId.MaxVelocity],
            6
        );
    }

    private static (long Turret, long Launcher) InstallVictimWeapons(Fixture f, long pilot, long ship)
    {
        using var action = StructureAttackTests.Scope(f);
        long turret = Install(Blaster, ship, InventoryFlag.HighSlot0, true);
        long launcher = Install(LightLauncher, ship, InventoryFlag.HighSlot1, true);
        _ = Install(LightMissile, launcher, InventoryFlag.Cargo, false);
        action.Commit();
        return (turret, launcher);

        long Install(int type, long at, InventoryFlag flag, bool fitted)
        {
            long item = InventoryPersistence.NextId(action.World);
            action.World.Add(
                new OwnedItem
                {
                    Id = item,
                    OwnerId = pilot,
                    LocationId = at,
                    FlagId = (int)flag,
                    TypeId = type,
                    Quantity = 1,
                    Online = fitted,
                    Singleton = fitted,
                }
            );
            return item;
        }
    }
}
