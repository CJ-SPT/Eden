using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Structures;
using Eden.World.Catalog;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Eden.Protocol.Tests.Structures.StructureDefenseFixture;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructurePointDefenseTests
{
    private const int Fortizar = 35833;
    private const int Battery = 35926;
    private const int AdvancedBattery = 47327;
    private const int Flak = 63195;
    private const int NpcType = 601;
    private const long DistantNpc = Npc + 1;
    private const double FullShield = 100_000;
    private const string PointDefense = "pointDefense";

    [Theory]
    [InlineData(Battery, 2500, 1500, 250)]
    [InlineData(AdvancedBattery, 3000, 1800, 312.5)]
    public void PointDefenseProfilesMatchPinnedCyclesAndFitting(int typeId, double range, double capacitor, double damage)
    {
        using var f = StructureAttackTests.Prepare();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var equipment = StructureEquipmentCatalog.Require(catalog, typeId);
        var charge = StructureChargeCatalog.Require(catalog, Flak);
        Assert.Equal(StructureWeaponRules.PointDefenseGroup, equipment.GroupId);
        Assert.Equal(ModuleRack.High, equipment.Fitting.Rack);
        Assert.True(equipment.Restrictions.Allows(Fortizar, StructureCatalog.Require(catalog, Fortizar).GroupId));
        Assert.False(equipment.Restrictions.Allows(35832, StructureCatalog.Require(catalog, 35832).GroupId));
        Assert.True(StructureAmmunitionRules.Compatible(equipment, charge));
        Assert.Equal(180000, equipment.Attributes[DogmaAttributeId.ReloadTime]);
        var profile = StructurePointDefense.Define(equipment.Attributes, charge.Attributes);
        Assert.Equal(range, profile.Range);
        Assert.Equal(capacitor, profile.Capacitor);
        Assert.Equal(12000, profile.Duration);
        Assert.Equal(200, profile.Rounds);
        Assert.Equal(new(damage, damage, damage, damage), profile.Damage);
    }

    [Fact]
    public void NativePointDefensePaysFlakAndCapacitorAndHitsOnlyLegalNearbyContacts()
    {
        using var f = StructureAttackTests.Prepare();
        PrepareCatalog(f);
        var structure = f.Online(Fortizar);
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long battery = f.Item(Battery);
        f.Db.Services.GetRequiredService<IPilotStructureFitting>().Fit(f.PilotId, structure.Id, [new(battery)]);
        var fit = f.Db.Services.GetRequiredService<IPilotStructureFitting>().Read(f.PilotId, structure.Id);
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        var ammo = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
        var loaded = ammo.Load(f.PilotId, structure.Id, [battery], [f.Item(Flak, 400)], f.ShipId);
        Assert.Equal(180000, loaded.ReloadMilliseconds);
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
        var token = gunner.StructureControl!.Token;
        object?[] activate = [battery, PointDefense, null, 2L];
        Assert.IsType<PilotCallResult.Failure>(Call("dogmaIM", "Activate", activate));
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(loaded.ReloadMilliseconds));
        using (var action = StructureAttackTests.Scope(f))
        {
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
            double radius = catalog.RequireItem(NpcType).Radius ?? 0;
            double boundary = structure.Position.X + structure.Profile.Radius + radius + 2500;
            AddContact(Npc, boundary);
            AddContact(DistantNpc, boundary + 1);
            action.Commit();

            void AddContact(long id, double x) =>
                action.World.Add(
                    new WorldNpc
                    {
                        Id = id,
                        SystemId = Fixture.SystemId,
                        TypeId = NpcType,
                        X = x,
                        HomeX = x,
                        ShieldCharge = FullShield,
                        UpdatedAt = f.Clock.Now,
                        MotionAt = f.Clock.Now,
                    }
                );
        }
        Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "Activate", activate));
        Assert.IsType<PilotCallResult.Success>(Call("dogmaIM", "Activate", activate));
        var effect = Assert.Single(
            gunner.Notifications.OfType<ShipEffectChanged>(),
            effect => effect.Effect.Effect == DogmaEffect.StructurePointDefense
        );
        Assert.True(effect.Active);
        Assert.Null(effect.Effect.TargetId);
        Assert.Empty(f.Db.Services.GetRequiredService<IPilotStructureTargeting>().Read(token).Targets);
        var visual = Assert.Single(f.Db.Services.GetRequiredService<StructureWeaponEffects>().Read(Fixture.SystemId));
        Assert.Equal(2500, visual.Pulse!.Range);
        Assert.Equal(200, Assert.Single(ammo.Read(f.PilotId, structure.Id)).Quantity);
        using (var action = StructureAttackTests.Scope(f))
        {
            Assert.Equal(
                fit.Attributes[DogmaAttributeId.CapacitorCapacity] - 1500,
                action.World.Set<SavedStructureEnergy>().Find(structure.Id)!.Charge
            );
            double shield = action.World.Set<WorldNpc>().Find(Npc)!.ShieldCharge;
            Assert.InRange(shield, FullShield - 1000, FullShield - 1);
            Assert.Equal(FullShield, action.World.Set<WorldNpc>().Find(DistantNpc)!.ShieldCharge);
            Assert.Null(action.World.Set<ShipHealthState>().Find(other.ShipId));
            StructureCombatTargets.Record(action.World, other.ShipId, structure.Id, f.Clock.Now, f.Clock.Now);
            action.Commit();
        }
        var first = Assert.Single(weapons.Read(token));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(first.ReadyAt - f.Clock.Now - 1));
        Assert.Empty(weapons.Advance(token, 2).Started);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        var next = Assert.Single(weapons.Advance(token, 2).Started);
        Assert.Equal(0, next.PointDefense!.Magazine.Quantity);
        Assert.Empty(ammo.Read(f.PilotId, structure.Id));
        using (var action = StructureAttackTests.Scope(f))
        {
            Assert.True(
                action.World.Set<ShipHealthState>().Find(other.ShipId) is not null
                    || action.World.Set<ShipLoss>().Find(other.ShipId) is not null
            );
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(12));
        var stopped = weapons.Advance(token, 3);
        Assert.Empty(stopped.Started);
        Assert.Single(stopped.Stopped);
        Assert.Empty(weapons.Read(token));
    }

    private static void PrepareCatalog(Fixture f)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw("UPDATE ItemType SET Capacity=1000000 WHERE Id=601");
        catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1000000 WHERE ItemTypeId=601 AND AttributeId=38");
        foreach (
            DogmaAttributeId attribute in new[]
            {
                DogmaAttributeId.ShieldCapacity,
                DogmaAttributeId.ArmorHitPoints,
                DogmaAttributeId.StructureHitPoints,
            }
        )
        {
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},{FullShield})");
        }
    }
}
