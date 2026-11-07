using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Ammunition;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class WeaponBankTests
{
    private static PilotWeaponBankStore Banks(AmmunitionTests.Fixture f) => new(new(f.Db.Path), f.Clock);

    private static long SecondGun(AmmunitionTests.Fixture f)
    {
        long gun = f.Buy(561, 1);
        f.Fitting.Fit(f.Pilot.PilotId, [gun], f.Pilot.StationId, (int)InventoryFlag.AutoFit);
        f.Fitting.SetOnline(f.Pilot.PilotId, f.Pilot.ShipId, gun, true);
        return gun;
    }

    private static void Link(AmmunitionTests.Fixture f, long second) =>
        Banks(f).Change(f.Pilot.PilotId, f.Pilot.ShipId, WeaponBankOperation.Link, f.Gun, second);

    [Fact]
    public void InsufficientGroupedReloadRollsBackStacksAndDeadlines()
    {
        using AmmunitionTests.Fixture f = new();
        long second = SecondGun(f);
        Link(f, second);
        Assert.Throws<InvalidDataException>(() => f.Load(quantity: 1));
        Assert.Empty(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
        Assert.Equal(1, f.Total(222));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    [Fact]
    public void APartialAmmoStackIsSharedAcrossTheWholeBank()
    {
        using AmmunitionTests.Fixture f = new();
        long second = SecondGun(f);
        Link(f, second);
        f.Load(quantity: 101);
        Assert.Equal(new[] { 51, 50 }, f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId).Select(m => m.Quantity));
        Assert.Equal(101, f.Total(222));
    }

    [Fact]
    public void ResidentFitsStayWarmAcrossGroupedShotsAndRefreshAfterAnAmmoChange()
    {
        using AmmunitionTests.Fixture f = new();
        long second = SecondGun(f);
        Link(f, second);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        long pilotId = f.Pilot.PilotId;
        using ResidentFitCache cache = new(new(f.Db.Path));
        _ = Fire();
        f.Clock.Milliseconds += 3000;
        using (var measure = SessionPerformance.Begin("grouped-warm", _ => { }, force: true)!)
        {
            Assert.Equal(2, Fire().Weapons.Count);
            Assert.Equal(0, measure.FittingCalculations);
        }
        f.Clock.Milliseconds += 3000;
        f.Db.Store.SetPosition(f.Pilot.ShipId, null);
        f.Ammo.Unload(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], f.Pilot.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now);
        f.Load(type: 215, quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        using (var measure = SessionPerformance.Begin("grouped-changed", _ => { }, force: true)!)
        {
            Assert.All(Fire().Weapons, weapon => Assert.Equal(215, weapon.ChargeTypeId));
            Assert.True(measure.FittingCalculations > 0);
        }
        f.Clock.Milliseconds += 3000;
        using (var measure = SessionPerformance.Begin("grouped-warm-again", _ => { }, force: true)!)
        {
            _ = Fire();
            Assert.Equal(0, measure.FittingCalculations);
        }

        CombatVolley Fire() => f.Combat.FireBank(pilotId, f.Gun, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5, WeaponActivation.AmmoFed);
    }

    [Theory]
    [InlineData(WeaponBankOperation.Link)]
    [InlineData(WeaponBankOperation.LinkAll)]
    public void NativeGroupingRepliesAndShipPrimesRetainBanksAcrossRestart(WeaponBankOperation operation)
    {
        using AmmunitionTests.Fixture f = new();
        long second = SecondGun(f);
        using var context = f.Context();
        WeaponBankHandler handler = new(Banks(f), f.Inventory, []);
        object?[] args = operation == WeaponBankOperation.Link ? [f.Pilot.ShipId, f.Gun, second] : [f.Pilot.ShipId];
        var reply = Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "dogmaIM", operation == WeaponBankOperation.Link ? "LinkWeapons" : "LinkAllWeapons", args)
        );
        var wire = Assert.IsType<WeaponBanksWire>(reply.Reply);
        Assert.Equal(second, Assert.Single(Assert.Single(wire.Banks).SlaveIds));
        var decoded = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(wire)));
        Assert.Equal(second, Assert.Single(Assert.IsType<List<object?>>(decoded[f.Gun])));
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "dogmaIM", "LinkAllWeapons", [f.Pilot.ShipId]));
        Assert.IsType<PilotCallResult.Success>(handler.Call(context, "dogmaIM", "LinkAllWeapons", [f.Pilot.ShipId]));
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(second, Assert.Single(Assert.Single(Banks(f).Read(f.Pilot.PilotId, f.Pilot.ShipId)).SlaveIds));
        using var scope = f.Db.Services.CreateScope();
        var all = scope.ServiceProvider.GetRequiredService<ShipDogmaBuilder>().AllInfo(f.Pilot);
        var state = Assert.IsType<object?[]>(all.ShipState.ToBlue());
        Assert.Single(Assert.IsType<WeaponBanksWire>(state[2]).Banks);
        var split = Assert.IsType<PilotCallResult.Success>(handler.Call(context, "dogmaIM", "UnlinkModule", [f.Pilot.ShipId, f.Gun]));
        Assert.Equal(second, split.Reply.ToBlue());
        Assert.Empty(Banks(f).Read(f.Pilot.PilotId, f.Pilot.ShipId));
    }

    [Fact]
    public void MergePeelAndDestroyKeepEveryWeaponInOneBank()
    {
        GroupableWeapon[] weapons = [new(1, 561), new(2, 561), new(3, 561), new(4, 561), new(5, 562)];
        WeaponBank[] initial = [new(1, new long[] { 2 }), new(3, new long[] { 4 })];
        var peeled = WeaponGrouping.Change(initial, weapons, WeaponBankOperation.Peel, 1, 3);
        Assert.Equal(new long[] { 2, 4 }, Assert.Single(peeled.Banks).SlaveIds);
        var merged = WeaponGrouping.Change(initial, weapons, WeaponBankOperation.Merge, 1, 3);
        Assert.Equal(new long[] { 2, 3, 4 }, Assert.Single(merged.Banks).SlaveIds);
        Assert.Throws<InvalidDataException>(() => WeaponGrouping.Change(initial, weapons, WeaponBankOperation.Link, 1, 5));
        Assert.Empty(WeaponGrouping.Change(merged.Banks, weapons, WeaponBankOperation.Destroy, 2).Banks);
        Assert.Empty(WeaponGrouping.Change(initial, weapons, WeaponBankOperation.UnlinkAll).Banks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedReloadVolleyAndUnloadAccountForEveryMagazine(bool resident)
    {
        using AmmunitionTests.Fixture f = new(resident);
        long second = SecondGun(f);
        Link(f, second);
        f.Load(quantity: 450);
        Assert.Equal(new[] { 200, 200 }, f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId).Select(m => m.Quantity));
        Assert.Throws<InvalidDataException>(() => Banks(f).Change(f.Pilot.PilotId, f.Pilot.ShipId, WeaponBankOperation.UnlinkAll));
        f.Clock.Milliseconds += 5000;
        f.Space();
        var volley = f.Combat.FireBank(f.Pilot.PilotId, f.Gun, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5, WeaponActivation.AmmoFed);
        Assert.Equal(2, volley.Weapons.Count);
        Assert.Equal(2, volley.Magazines.Count);
        Assert.All(volley.Magazines, m => Assert.Equal(199, m.Quantity));
        Assert.True(volley.Shot.Applied > 0);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.All(new[] { f.Gun, second }, id => Assert.Equal(ModuleCooldownKind.Cycle, world.Set<ModuleCooldown>().Find(id)!.Kind));
            Assert.Equal(volley.Shot.Capacitor, world.Set<ShipEnergy>().Find(f.Pilot.ShipId)!.Charge);
        }
        Assert.Throws<InvalidDataException>(() =>
            f.Combat.FireBank(f.Pilot.PilotId, second, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5)
        );
        Assert.Equal(448, f.Total(222));
        f.Clock.Milliseconds += 10000;
        f.Db.Store.SetPosition(f.Pilot.ShipId, null);
        f.Ammo.Unload(f.Pilot.PilotId, f.Pilot.ShipId, [f.Gun], f.Pilot.StationId, (int)InventoryFlag.Hangar, null, f.Clock.Now);
        Assert.Empty(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
        Assert.Equal(448, f.Total(222));
    }

    [Theory]
    [InlineData("empty", false)]
    [InlineData("capacitor", false)]
    [InlineData("offline", false)]
    [InlineData("reload", false)]
    [InlineData("different-ammo", false)]
    [InlineData("empty", true)]
    [InlineData("capacitor", true)]
    [InlineData("offline", true)]
    [InlineData("reload", true)]
    [InlineData("different-ammo", true)]
    public void ARejectedBankRollsBackEarlierMemberPayments(string reason, bool resident)
    {
        using AmmunitionTests.Fixture f = new(resident);
        long second = SecondGun(f);
        Link(f, second);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        using (WorldDbContext world = new(f.Db.Path))
        {
            var charge = world.Set<OwnedItem>().Single(i => i.LocationId == second);
            if (reason == "empty")
            {
                world.Remove(charge);
            }
            if (reason == "different-ammo")
            {
                charge.TypeId = 215;
            }
            if (reason == "offline")
            {
                world.Set<OwnedItem>().Find(second)!.Online = false;
            }
            if (reason == "reload")
            {
                world.Set<ModuleCooldown>().Find(second)!.ReadyAt = f.Clock.Now + TimeSpan.TicksPerSecond;
            }
            if (reason == "capacitor")
            {
                world.Add(
                    new ShipEnergy
                    {
                        ShipId = f.Pilot.ShipId,
                        Charge = 1.2,
                        UpdatedAt = f.Clock.Now,
                    }
                );
            }
            world.SaveChanges();
        }
        int before = f.Total(222);
        Assert.Throws<InvalidDataException>(() =>
            f.Combat.FireBank(f.Pilot.PilotId, f.Gun, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5, WeaponActivation.AmmoFed)
        );
        Assert.Equal(before, f.Total(222));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(ModuleCooldownKind.Reload, saved.Set<ModuleCooldown>().Find(f.Gun)!.Kind);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Equal(reason == "capacitor" ? 1.2 : (double?)null, saved.Set<ShipEnergy>().Find(f.Pilot.ShipId)?.Charge);
    }

    [Fact]
    public void ForeignAndOfflineGroupingIsRejectedAndOffliningPrunesSavedLinks()
    {
        using AmmunitionTests.Fixture f = new();
        long second = SecondGun(f);
        Assert.Throws<InvalidDataException>(() => Banks(f).Change(90000002, f.Pilot.ShipId, WeaponBankOperation.LinkAll));
        f.Fitting.SetOnline(f.Pilot.PilotId, f.Pilot.ShipId, second, false);
        Assert.Throws<InvalidDataException>(() => Link(f, second));
        Assert.Empty(Banks(f).Read(f.Pilot.PilotId, f.Pilot.ShipId));
        f.Fitting.SetOnline(f.Pilot.PilotId, f.Pilot.ShipId, second, true);
        Link(f, second);
        using var scope = f.Db.Services.CreateScope();
        using var context = f.Context();
        var operations = scope.ServiceProvider.GetRequiredService<FittingOperations>();
        Assert.IsType<PilotCallResult.Success>(operations.SetOnline(context, f.Pilot.ShipId, second, false));
        Assert.Empty(Banks(f).Read(f.Pilot.PilotId, f.Pilot.ShipId));
        Assert.Empty(Assert.Single(context.Notifications.OfType<WeaponBanksChanged>()).Banks);
    }

    [Theory]
    [InlineData(68)]
    [InlineData(69)]
    public void LegacyWorldGetsAnEmptyBankTableAndRetainsInventory(int version)
    {
        using AmmunitionTests.Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldWeaponLink");
            world.Database.ExecuteSqlRaw(version == 68 ? "PRAGMA user_version=68" : "PRAGMA user_version=69");
        }
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Empty(Banks(f).Read(f.Pilot.PilotId, f.Pilot.ShipId));
        Assert.Contains(f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId), i => i.Id == f.Gun);
        _ = WorldInitialization.Open(f.Db.Path);
    }

    [Fact]
    public void GroupedTurretsRepeatAndStopTogetherWithOneCombatMessagePerVolley()
    {
        using AmmunitionTests.Fixture f = new(liveWorld: true);
        long second = SecondGun(f);
        Link(f, second);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        var pilot = f.Pilot;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), [], encounters: f.Db.Services.GetRequiredService<INpcEncounters>()),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        TargetingHandler targeting = new(f.Clock, f.Fitting, new());
        Assert.IsType<object?[]>(targeting.CallWire(context, "dogmaIM", "AddTarget", [f.Npc.Id]));
        f.Clock.Milliseconds += 5000;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(f.Npc.Id));
        CombatHandler handler = new(f.Clock, f.Combat, targeting);
        context.Notifications.Clear();
        context.NotificationsBeforeFlight.Clear();
        Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [f.Gun, "projectileFired", f.Npc.Id, 100L]));
        Assert.Equal(398, f.Total(222));
        Assert.NotNull(handler.ActiveEffect(pilot, second));
        Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [second, "projectileFired", f.Npc.Id, 100L]));
        Assert.Equal(398, f.Total(222));
        Assert.Equal(2, context.Notifications.Count(n => n.Name == "OnGodmaShipEffect"));
        Assert.True(
            Assert.IsType<DamageMessageNotification>(Assert.Single(context.NotificationsBeforeFlight)).Message
                is OutgoingCombatMessage { Banked: true }
        );
        f.Clock.Milliseconds += 3000;
        WeaponBankHandler grouping = new(Banks(f), f.Inventory, [handler]);
        Assert.IsType<PilotCallResult.Failure>(grouping.Call(context, "dogmaIM", "UnlinkAllModules", [pilot.ShipId]));
        handler.Advance(context);
        Assert.Equal(396, f.Total(222));
        Assert.Equal(2, context.NotificationsBeforeFlight.Count);
        Assert.Equal(2, context.Notifications.Count(n => n.Name == "OnGodmaShipEffect"));
        Assert.Null(handler.CallWire(context, "dogmaIM", "Deactivate", [second, "projectileFired"]));
        f.Clock.Milliseconds += 3000;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(pilot, f.Gun));
        Assert.Null(handler.ActiveEffect(pilot, second));
        Assert.Equal(396, f.Total(222));
        Assert.Equal(4, context.Notifications.Count(n => n.Name == "OnGodmaShipEffect"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FatalVolleyPaysEveryWeaponAndSettlesTheKillOnce(bool resident)
    {
        using AmmunitionTests.Fixture f = new(resident);
        long second = SecondGun(f);
        Link(f, second);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO DogmaAttribute VALUES (481,'entityKillBounty')");
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (30380,481,3000)");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            var npc = world.Set<WorldNpc>().Find(f.Npc.Id)!;
            npc.ShieldCharge = 0;
            npc.ArmorDamage = 15;
            npc.StructureDamage = 19;
            npc.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        var volley = f.Combat.FireBank(f.Pilot.PilotId, f.Gun, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0, WeaponActivation.AmmoFed);
        Assert.True(volley.Shot.Destroyed);
        Assert.Equal(398, f.Total(222));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<NpcBounty>());
        Assert.Single(saved.Set<WorldWreck>());
    }

    [Theory]
    [InlineData(450, 239)]
    [InlineData(484, 178)]
    public void EnergyAndProjectileBanksUseTheirExistingChargeRules(int weapon, int charge)
    {
        using var f = weapon == 450 ? EnergyTierTests.WithTiers(weapon, charge) : ProjectileTierTests.WithTiers(weapon, charge);
        long first = f.Fit(weapon);
        long second = f.Fit(weapon);
        PilotWeaponBankStore banks = new(new(f.Db.Path), f.Clock);
        banks.Change(f.PilotId, f.ShipId, WeaponBankOperation.Link, first, second);
        var ammo = f.Db.Services.GetRequiredService<IPilotAmmunition>();
        long stack = f.Grant(charge, weapon == 450 ? 2 : 1000);
        ammo.Load(f.PilotId, f.ShipId, [first], [stack], f.StationId, f.Clock.Now);
        var before = ammo.Read(f.PilotId, f.ShipId).ToDictionary(m => m.ModuleId, m => m.Quantity);
        Assert.Equal(2, before.Count);
        f.Clock.Milliseconds += 20000;
        var npc = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.ShipId, npc.Position + new SpacePosition(100, 0, 0), npc.SystemId);
        var volley = f
            .Db.Services.GetRequiredService<IPilotCombat>()
            .FireBank(f.PilotId, first, npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
        Assert.Equal(2, volley.Weapons.Count);
        Assert.All(volley.Magazines, m => Assert.Equal(before[m.ModuleId] - (weapon == 450 ? 0 : 1), m.Quantity));
    }

    [Fact]
    public void GroupedMissileLaunchesPersistSeparateProjectilesAndRollbackOnAnEmptySlave()
    {
        using var f = MissileCombatTests.Prepare(499, 212);
        long first = MissileCombatTests.Load(f, 499, 212);
        long second = MissileCombatTests.Load(f, 499, 212);
        PilotWeaponBankStore banks = new(new(f.Db.Path), f.Clock);
        banks.Change(f.PilotId, f.ShipId, WeaponBankOperation.Link, first, second);
        var npc = MissileCombatTests.Space(f);
        var missiles = MissileCombatTests.Missiles(f);
        var launches = missiles.LaunchBank(
            f.PilotId,
            first,
            npc.Id,
            new HashSet<long> { npc.Id },
            new(SpacePosition.Zero, 295, new(0, 0, -1), 42),
            f.Clock.Now
        );
        Assert.Equal(2, launches.Count);
        Assert.All(launches, l => Assert.Equal(2, l.Magazine.Quantity));
        Assert.Equal(2, missiles.List(npc.SystemId).Count);
        f.Clock.Milliseconds += 35000;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Where(i => i.LocationId == second).ExecuteDelete();
        }
        Assert.Throws<InvalidDataException>(() =>
            missiles.LaunchBank(
                f.PilotId,
                first,
                npc.Id,
                new HashSet<long> { npc.Id },
                new(SpacePosition.Zero, 295, new(0, 0, -1), 42),
                f.Clock.Now
            )
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(2, saved.Set<WorldMissile>().Count());
        Assert.Equal(2, saved.Set<OwnedItem>().Single(i => i.LocationId == first).Quantity);
    }

    [Fact]
    public void ProductionMissileControllerRepeatsAndStopsTheWholeBank()
    {
        using var f = MissileCombatTests.Prepare(499, 212);
        long first = MissileCombatTests.Load(f, 499, 212);
        long second = MissileCombatTests.Load(f, 499, 212);
        PilotWeaponBankStore banks = new(new(f.Db.Path), f.Clock);
        banks.Change(f.PilotId, f.ShipId, WeaponBankOperation.Link, first, second);
        var npc = MissileCombatTests.Space(f, 10000);
        using var scope = f.Db.Services.CreateScope();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(p),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        Assert.IsType<object?[]>(targeting.CallWire(context, "dogmaIM", "AddTarget", [npc.Id]));
        f.Clock.Milliseconds += 5000;
        targeting.Advance(context);
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var controller = scope.ServiceProvider.GetRequiredService<MissileHandler>();
        context.Notifications.Clear();
        Assert.Null(activation.CallWire(context, "dogmaIM", "Activate", [first, "useMissiles", npc.Id, 100L]));
        Assert.Null(activation.CallWire(context, "dogmaIM", "Activate", [second, "useMissiles", npc.Id, 100L]));
        Assert.Equal(2, MissileCombatTests.Missiles(f).List(npc.SystemId).Count);
        Assert.NotNull(controller.ActiveEffect(pilot, second));
        f.Clock.Milliseconds += 15000;
        controller.Advance(context);
        Assert.Equal(2, MissileCombatTests.Missiles(f).List(npc.SystemId).Count);
        Assert.Equal(2, context.Notifications.Count(n => n.Name == "OnGodmaShipEffect"));
        Assert.Null(activation.CallWire(context, "dogmaIM", "Deactivate", [second, "useMissiles"]));
        f.Clock.Milliseconds += 15000;
        controller.Advance(context);
        Assert.Empty(MissileCombatTests.Missiles(f).List(npc.SystemId));
        Assert.Null(controller.ActiveEffect(pilot, first));
        Assert.Null(controller.ActiveEffect(pilot, second));
        Assert.Equal(4, context.Notifications.Count(n => n.Name == "OnGodmaShipEffect"));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(4, saved.Set<WorldMissile>().Count());
    }
}
