using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Repair;

public sealed class ShieldBoostTests
{
    [Fact]
    public void AutomaticRepeatsKeepHudEffectActiveUntilARealStop()
    {
        using Fixture f = new();
        using var context = f.Client();
        ShieldBoostHandler handler = new(f.Clock, f.Boosting);
        Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [f.Module, ShieldBoostWireDefinitions.EffectName, null, 100L]));
        Assert.True(Assert.Single(context.Notifications.OfType<ShipEffectChanged>()).Start);
        context.Notifications.Clear();
        for (int cycle = 0; cycle < 2; cycle++)
        {
            f.Clock.Milliseconds += 2500;
            handler.Advance(context);
            Assert.NotNull(handler.ActiveEffect(context.Pilot, f.Module));
            Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        }
        Assert.Null(handler.CallWire(context, "dogmaIM", "Deactivate", [f.Module, ShieldBoostWireDefinitions.EffectName]));
        f.Clock.Milliseconds += 2500;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, f.Module));
        Assert.False(Assert.Single(context.Notifications.OfType<ShipEffectChanged>()).Start);
    }

    [Fact]
    public void MediumSlotSwapsRetainOnlineCostsAndDoNotActivateOrRepair()
    {
        using Fixture f = new();
        f.Db.Store.SetPosition(f.Pilot.ShipId, null);
        long other = Assert
            .Single(f.Market.Buy(f.Pilot.PilotId, f.Pilot.StationId, [new((int)ShieldBoosterType.SmallI, 100000, 1)]).Changes)
            .Item.Id;
        f.Fitting.Fit(f.Pilot.PilotId, [other], f.Pilot.StationId, (int)InventoryFlag.AutoFit);
        var before = f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId);
        var attributes = f.Fitting.ShipAttributes(f.Pilot.PilotId);
        var health = f.Health.Read(f.Pilot.PilotId, f.Clock.Now);
        int destination = Assert.Single(before, i => i.Id == other).FlagId;
        var changes = f.Fitting.Fit(f.Pilot.PilotId, [f.Module], f.Pilot.ShipId, destination);
        Assert.Equal(2, changes.Count);
        Assert.Equal(
            before.Select(i => i with { FlagId = i.Id == f.Module ? destination : (int)InventoryFlag.MediumSlot0 }),
            f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId)
        );
        Assert.Equal(attributes.OrderBy(a => a.Key), f.Fitting.ShipAttributes(f.Pilot.PilotId).OrderBy(a => a.Key));
        Assert.Equal(health, f.Health.Read(f.Pilot.PilotId, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public Clock Clock { get; } = new();
        public IPilotShieldBoosting Boosting => Db.Services.GetRequiredService<IPilotShieldBoosting>();
        public IPilotHealth Health => Db.Services.GetRequiredService<IPilotHealth>();
        public IPilotFitting Fitting => Db.Services.GetRequiredService<IPilotFitting>();
        public IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();
        public IPilotMarket Market => Db.Services.GetRequiredService<IPilotMarket>();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public long Module { get; }

        public Fixture()
        {
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (40,'Shield Booster',7);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (399,'Small Shield Booster I',40),(3416,'Shield Operation',257),
                        (21059,'Shield Compensation',257);
                    INSERT INTO DogmaAttribute(Id,Name) VALUES (6,'capacitorNeed'),(11,'powerOutput'),(13,'medSlots'),(14,'hiSlots'),
                        (30,'power'),(48,'cpuOutput'),(50,'cpu'),(68,'shieldBonus'),(73,'duration'),(182,'requiredSkill1'),
                            (277,'requiredSkill1Level'),(851,'shieldBoostCapacitorBonus');
                    INSERT INTO TypeAttribute VALUES (601,11,23),(601,13,2),(601,14,2),(601,48,135),
                        (3651,50,65),(3651,30,2),(399,6,20),(399,30,2),(399,50,25),(399,68,26),(399,73,2000),(399,182,3416),(399,277,1),
                        (3416,275,1),(21059,275,2),(21059,851,-2);
                    INSERT INTO SourceRecord VALUES ('client/types','399','{{"published":1,"marketGroupID":1,"volume":5}}'),
                        ('client/stationoperations','14','{{"services":[7,17,13]}}');
                    """
                );
            }
            using (WorldDbContext world = new(Db.Path))
            {
                world.Database.ExecuteSqlRaw("INSERT INTO WorldPilotSkill VALUES (90000001,3416,250)");
                world.Add(new PilotWallet { PilotId = Pilot.PilotId, Cents = 1000000 });
                world.SaveChanges();
            }
            Module = Assert
                .Single(Market.Buy(Pilot.PilotId, Pilot.StationId, [new((int)ShieldBoosterType.SmallI, 100000, 1)]).Changes)
                .Item.Id;
            Fitting.Fit(Pilot.PilotId, [Module], Pilot.StationId, (int)InventoryFlag.AutoFit);
            Fitting.SetOnline(Pilot.PilotId, Pilot.ShipId, Module, true);
            Db.Store.SetPosition(Pilot.ShipId, SpacePosition.Zero);
            Damage();
        }

        public void Damage(double shield = 25)
        {
            using WorldDbContext world = new(Db.Path);
            var saved =
                world.Set<ShipHealthState>().Find(Pilot.ShipId) ?? world.Set<ShipHealthState>().Add(new() { ShipId = Pilot.ShipId }).Entity;
            saved.ShieldCharge = shield;
            saved.ArmorDamage = 12;
            saved.StructureDamage = 9;
            saved.UpdatedAt = Clock.Now;
            world.SaveChanges();
        }

        public ShieldBoost Boost() => Boosting.Boost(Pilot.PilotId, Module, Clock.Now);

        public PilotCallContext Client(SharedSpace? space = null)
        {
            var pilot = Pilot;
            PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, Clock, space ?? new(), Db.Objects, health: Health),
                7,
                SignedSettings.Empty
            );
            context.Select(pilot);
            context.Flight.Snapshot();
            context.Flight.Updates.Clear();
            return context;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void MarketPurchaseFitsAMidSlotAlongsideTurretsAndUnfitsWithIdentityIntact()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        f.Db.Store.SetPosition(pilot.ShipId, null);
        var miner = f.Inventory.List(pilot.PilotId, pilot.StationId).Single(i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        f.Fitting.Fit(pilot.PilotId, [miner.Id], pilot.StationId, (int)InventoryFlag.HighSlot0);
        f.Fitting.SetOnline(pilot.PilotId, pilot.ShipId, miner.Id, true);
        var booster = f.Inventory.List(pilot.PilotId, pilot.ShipId).Single(i => i.Id == f.Module);
        Assert.Equal((int)InventoryFlag.MediumSlot0, booster.FlagId);
        Assert.True(booster.Singleton && booster.Online);
        Assert.Equal(1, f.Fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.TurretSlotsLeft]);
        Assert.Equal(90, f.Fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.CpuLoad]);
        Assert.Equal(4, f.Fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.PowerLoad]);
        Assert.Equal(999900, f.Market.Balance(pilot.PilotId));
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(1000000, f.Market.Orders(pilot.PilotId).Single(o => o.TypeId == (int)ShieldBoosterType.SmallI).Remaining);
        var change = Assert.Single(f.Inventory.Move(pilot.PilotId, [f.Module], pilot.ShipId, pilot.ShipId, (int)InventoryFlag.Cargo, null));
        Assert.Equal(f.Module, change.Item.Id);
        Assert.True(change.Item.Singleton);
        Assert.False(change.Item.Online);
        Assert.Equal(65, f.Fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.CpuLoad]);
    }

    [Fact]
    public void MidSlotLimitsWrongRacksSkillsAndCombinedCpuAreEnforcedAtomically()
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        f.Db.Store.SetPosition(pilot.ShipId, null);
        long stack = Assert
            .Single(f.Market.Buy(pilot.PilotId, pilot.StationId, [new((int)ShieldBoosterType.SmallI, 100000, 2)]).Changes)
            .Item.Id;
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.HighSlot1));
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.MediumSlot7));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DELETE FROM WorldPilotSkill WHERE TypeId=3416");
        }
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.AutoFit));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("INSERT INTO WorldPilotSkill VALUES (90000001,3416,250)");
        }
        var second = f.Fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.AutoFit)[^1].Item;
        Assert.Equal((int)InventoryFlag.MediumSlot0 + 1, second.FlagId);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(pilot.PilotId, [stack], pilot.StationId, (int)InventoryFlag.AutoFit));
        Assert.Single(f.Inventory.List(pilot.PilotId, pilot.StationId), i => i.Id == stack && i.Quantity == 1);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=49 WHERE ItemTypeId=601 AND AttributeId=48");
        }
        Assert.Throws<InvalidDataException>(() => f.Fitting.SetOnline(pilot.PilotId, pilot.ShipId, second.Id, true));
        Assert.False(f.Inventory.List(pilot.PilotId, pilot.ShipId).Single(i => i.Id == second.Id).Online);
    }

    [Fact]
    public void BoostPaysCapacitorImmediatelyClampsShieldAndPreservesArmorHullAndWallet()
    {
        using Fixture f = new();
        f.Damage(190);
        var result = f.Boost();
        Assert.Equal(10, result.Restored, 8);
        Assert.Equal(200, result.Health.Remaining.Shield);
        Assert.Equal(110, result.Capacitor);
        Assert.Equal(113, result.Health.Remaining.Armor);
        Assert.Equal(166, result.Health.Remaining.Structure);
        Assert.Equal(999900, f.Market.Balance(f.Pilot.PilotId));
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() => f.Boost());
        f.Clock.Milliseconds = 2000;
        Assert.Equal(0, f.Boost().Restored);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(12, saved.Set<ShipHealthState>().Single().ArmorDamage);
        Assert.Equal(9, saved.Set<ShipHealthState>().Single().StructureDamage);
        Assert.Single(saved.Set<ModuleCooldown>());
    }

    [Fact]
    public void ShieldCompensationUsesCatalogPercentAndTrainedLevel()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = f.Pilot.PilotId,
                    TypeId = (int)ShieldBoostSkill.Compensation,
                    Points = 16000,
                }
            );
            world.SaveChanges();
        }
        Assert.Equal(18.8, f.Boost().Booster.CapacitorNeed, 8);
        Assert.Equal(
            111.2,
            f.Db.Services.GetRequiredService<Eden.Gameplay.Ships.IPilotShipResources>().Capacitor(f.Pilot.PilotId, f.Clock.Now),
            8
        );
    }

    [Fact]
    public async Task CompetingBoostCallsPayAndHealOnce()
    {
        using Fixture f = new();
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            return f.Boost();
                        }
                        catch (InvalidDataException)
                        {
                            return null;
                        }
                    })
                )
        );
        Assert.Single(results, r => r is not null);
        Assert.Equal(51, f.Health.Read(f.Pilot.PilotId, f.Clock.Now).Remaining.Shield, 8);
        Assert.Equal(
            110,
            f.Db.Services.GetRequiredService<Eden.Gameplay.Ships.IPilotShipResources>().Capacitor(f.Pilot.PilotId, f.Clock.Now)
        );
    }

    [Fact]
    public async Task NpcDamageAndBoostSerializeWithoutLosingEitherHealthChange()
    {
        using Fixture f = new();
        NpcEncounterTests.Prepare(f.Db);
        var npc = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.Pilot.ShipId, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new NpcAggression { NpcId = npc.Id, TargetShipId = f.Pilot.ShipId });
            world.SaveChanges();
        }
        var incoming = Task.Run(() =>
            f
                .Db.Services.GetRequiredService<INpcCombat>()
                .Fire(f.Pilot.PilotId, f.Pilot.ShipId, npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5, 1)
        );
        var boost = Task.Run(f.Boost);
        await Task.WhenAll(incoming, boost);
        Assert.Equal(51 - (await incoming)!.Applied, f.Health.Read(f.Pilot.PilotId, f.Clock.Now).Remaining.Shield, 8);
        Assert.Equal(26, (await boost).Restored);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("cargo")]
    [InlineData("high slot")]
    [InlineData("docked")]
    [InlineData("foreign")]
    public void InvalidActivationNeverChangesHealthOrCapacitor(string reason)
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            var module = world.Set<OwnedItem>().Find(f.Module)!;
            switch (reason)
            {
                case "offline":
                    module.Online = false;
                    break;
                case "cargo":
                    module.FlagId = (int)InventoryFlag.Cargo;
                    break;
                case "high slot":
                    module.FlagId = (int)InventoryFlag.HighSlot0;
                    break;
                case "foreign":
                    module.OwnerId = 90000002;
                    break;
            }
            world.SaveChanges();
        }
        if (reason == "docked")
        {
            f.Db.Store.SetPosition(f.Pilot.ShipId, null);
        }
        Assert.Throws<InvalidDataException>(() => f.Boost());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(25, saved.Set<ShipHealthState>().Single().ShieldCharge);
        Assert.Empty(saved.Set<ShipEnergy>());
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    [Fact]
    public void InsufficientCapacitorRollsBackHealthAndCooldown()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipEnergy
                {
                    ShipId = f.Pilot.ShipId,
                    Charge = 1,
                    UpdatedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Boost());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(25, saved.Set<ShipHealthState>().Single().ShieldCharge);
        Assert.Equal(1, saved.Set<ShipEnergy>().Single().Charge);
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    [Theory]
    [InlineData(DogmaAttributeId.Duration, -1)]
    [InlineData(DogmaAttributeId.ShieldBonus, 0)]
    [InlineData(DogmaAttributeId.CapacitorNeed, -1)]
    public void InvalidCatalogAttributesDoNotPayOrHeal(DogmaAttributeId attribute, double value)
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value={value} WHERE ItemTypeId=399 AND AttributeId={(int)attribute}");
        }
        Assert.Throws<InvalidDataException>(() => f.Boost());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(25, saved.Set<ShipHealthState>().Single().ShieldCharge);
        Assert.Empty(saved.Set<ShipEnergy>());
        Assert.Empty(saved.Set<ModuleCooldown>());
    }

    [Fact]
    public void NativeSelfActivationStopsAfterPaidCycleAndReplicatesHealingToObservers()
    {
        using Fixture f = new();
        SharedSpace space = new();
        using var context = f.Client(space);
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, new(100, 0, 0));
        using FlightSession observer = new(f.Db.Store, f.Db.Store.FindPilot(1000002)!, f.Clock, space, [], health: f.Health);
        observer.Snapshot();
        observer.Updates.Clear();
        ShieldBoostHandler handler = new(f.Clock, f.Boosting);
        SharedTargetLocks locks = new();
        using TargetingHandler targeting = new(f.Clock, f.Fitting, locks);
        using var activationScope = f.Db.Services.CreateScope();
        ModuleActivationHandler activation = new(
            new(f.Clock, f.Db.Services.GetRequiredService<IPilotMining>(), targeting),
            new(f.Clock, f.Db.Services.GetRequiredService<IPilotCombat>(), targeting),
            handler,
            activationScope.ServiceProvider.GetRequiredService<FittingOperations>(),
            new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotPropulsion>())
        );
        object? Activate() =>
            activation.CallWire(context, "dogmaIM", "Activate", [f.Module, ShieldBoostWireDefinitions.EffectName, null, 100L]);
        Assert.Null(Activate());
        Assert.Null(Activate());
        Assert.Equal(51, f.Health.Read(f.Pilot.PilotId, f.Clock.Now).Remaining.Shield, 8);
        Assert.Equal((int)DogmaEffect.ShieldBoosting, (int)handler.ActiveEffect(context.Pilot, f.Module)!.Effect);
        observer.RefreshPeers();
        var actions = observer.WireUpdates().Cast<object?[]>().Select(u => (object?[])u[1]!).ToArray();
        Assert.Contains(actions, a => Equals(a[0], "OnDamageStateChange") && Equals(((object?[])a[1]!)[0], context.Pilot.ShipId));
        Assert.Contains(
            actions,
            a => Equals(a[0], "OnSpecialFX") && Equals(((object?[])a[1]!)[5], ShieldBoostWireDefinitions.EffectGraphic)
        );
        Assert.Null(activation.CallWire(context, "dogmaIM", "Deactivate", [f.Module, ShieldBoostWireDefinitions.EffectName]));
        Assert.NotNull(handler.ActiveEffect(context.Pilot, f.Module));
        f.Clock.Milliseconds = 2000;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, f.Module));
        Assert.Contains(
            context.Notifications,
            n => n.Name == "OnGodmaShipEffect" && Equals(n.Arguments[1], (int)DogmaEffect.ShieldBoosting) && Equals(n.Arguments[3], false)
        );
        Assert.NotEmpty(BlueMarshal.Encode(context.Notifications.Select(n => (object?)n.Arguments).ToList()));
    }

    [Fact]
    public void RepeatDoesNotCatchUpMissedCyclesAndStopsWhenNextCycleCannotBePaid()
    {
        using Fixture f = new();
        using var context = f.Client();
        ShieldBoostHandler handler = new(f.Clock, f.Boosting);
        Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [f.Module, ShieldBoostWireDefinitions.EffectName, null, 100L]));
        f.Clock.Milliseconds = 60000;
        handler.Advance(context);
        Assert.NotNull(handler.ActiveEffect(context.Pilot, f.Module));
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(f.Clock.Now, saved.Set<ShipHealthState>().Single().UpdatedAt);
            Assert.InRange(saved.Set<ShipEnergy>().Single().Charge, 100, 110);
            var energy = saved.Set<ShipEnergy>().Single();
            energy.Charge = 0;
            energy.UpdatedAt = f.Clock.Now;
            saved.SaveChanges();
        }
        f.Clock.Milliseconds += 2000;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, f.Module));
        using WorldDbContext failed = new(f.Db.Path);
        Assert.Equal(0, failed.Set<ShipEnergy>().Single().Charge);
        Assert.Equal(f.Clock.Now - 2000 * TimeSpan.TicksPerMillisecond, failed.Set<ShipHealthState>().Single().UpdatedAt);
    }

    [Fact]
    public void NativePrimeIncludesOnlineMidSlotAttributesAndItsActiveShieldEffect()
    {
        using Fixture f = new();
        using var context = f.Client();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Null(
            router.CallWire(
                context,
                "dogmaIM",
                "Activate",
                new object?[] { f.Module, ShieldBoostWireDefinitions.EffectName, null, 0L },
                _ => { }
            )
        );
        var info = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(router.CallWire(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { })).State
        );
        var modules = Assert.IsType<Dictionary<long, object?>>(info["shipInfo"]);
        var module = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(modules[f.Module]).State);
        var attributes = Assert.IsType<Dictionary<long, object?>>(module["attributes"]);
        Assert.Equal(26d, attributes[(long)DogmaAttributeId.ShieldBonus]);
        Assert.Equal(20d, attributes[(long)DogmaAttributeId.CapacitorNeed]);
        var effects = Assert.IsType<Dictionary<long, object?>>(module["activeEffects"]);
        Assert.True(effects.ContainsKey((long)DogmaEffect.Online));
        Assert.True(effects.ContainsKey((long)DogmaEffect.ShieldBoosting));
        Assert.NotEmpty(BlueMarshal.Encode(info));
    }

    [Fact]
    public void BoosterIsWarpSafeButStopsWhenItsHullBeginsDestruction()
    {
        using Fixture f = new();
        using var context = f.Client();
        Assert.Null(context.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)context.Pilot.StationId]));
        Assert.False(context.Flight.CanTarget);
        Assert.True(context.Flight.CanUseSelfModules);
        ShieldBoostHandler handler = new(f.Clock, f.Boosting);
        Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [f.Module, ShieldBoostWireDefinitions.EffectName, null, 100L]));
        Assert.NotNull(handler.ActiveEffect(context.Pilot, f.Module));
        context.Flight.BeginShipLoss();
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, f.Module));
        Assert.IsType<PilotCallResult.Failure>(
            handler.CallWire(context, "dogmaIM", "Activate", [f.Module, ShieldBoostWireDefinitions.EffectName, null, 0L])
        );
    }

    [Fact]
    public void FatalRetaliationStopsTheBoosterAndClearsItsCooldownEvenIfTheModuleDrops()
    {
        using Fixture f = new();
        NpcEncounterTests.Prepare(f.Db);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        var npc = encounters.List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.Pilot.ShipId, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (29,'Capsule',6);
                INSERT INTO ItemType(Id,Name,GroupId,Mass,Radius,Capacity) VALUES (670,'Capsule',29,32000,2,0),
                    (26510,'Caldari Rookie ship Wreck',186,10000,14,27500);
                INSERT INTO TypeAttribute SELECT 670,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601;
                UPDATE TypeAttribute SET Value=1000 WHERE ItemTypeId=30380 AND AttributeId=64;
                UPDATE TypeAttribute SET Value=10000 WHERE ItemTypeId=30380 AND AttributeId=54;
                UPDATE SourceRecord SET Data='{{"published":1,"marketGroupID":1,"volume":15000,"wreckTypeID":26510}}'
                    WHERE Dataset='client/types' AND Key='601';
                """
            );
        }
        var pilot = f.Pilot;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, f.Clock, new(), [], health: f.Health, encounters: encounters),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        ShieldBoostHandler shields = new(f.Clock, f.Boosting);
        Assert.Null(shields.CallWire(context, "dogmaIM", "Activate", [f.Module, ShieldBoostWireDefinitions.EffectName, null, 100L]));
        SharedTargetLocks locks = new();
        using TargetingHandler targeting = new(f.Clock, f.Fitting, locks);
        using NpcCombatHandler combat = new(
            f.Clock,
            f.Db.Services.GetRequiredService<INpcCombat>(),
            locks,
            new ShipLossCoordinator(
                new(f.Clock, f.Db.Services.GetRequiredService<IPilotMining>(), targeting),
                new(f.Clock, f.Db.Services.GetRequiredService<IPilotCombat>(), targeting),
                shields,
                new(f.Clock, f.Db.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotPropulsion>()),
                new Eden.Protocol.Docking.DeferredRefitPrimes(
                    new Eden.Protocol.Ammunition.MagazineWire(
                        f.Clock,
                        f.Fitting,
                        f.Db.Services.GetRequiredService<IPilotCombat>(),
                        f.Db.Services.GetRequiredService<IPilotInventory>()
                    ),
                    f.Db.Services.GetRequiredService<Eden.Gameplay.Ammunition.IPilotAmmunition>()
                )
            ),
            new()
        );
        combat.Enter(context);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var health = world.Set<ShipHealthState>().Single();
            health.ShieldCharge = 0;
            health.ArmorDamage = 125;
            health.StructureDamage = 174;
            world.Add(new NpcAggression { NpcId = npc.Id, TargetShipId = pilot.ShipId });
            world.SaveChanges();
        }
        combat.Advance(context);
        Assert.Equal(FleetDefinitions.CapsuleType, context.Pilot.ShipTypeId);
        Assert.Null(shields.ActiveEffect(context.Pilot, f.Module));
        Assert.Contains(
            context.Notifications,
            n => n.Name == "OnGodmaShipEffect" && Equals(n.Arguments[1], (int)DogmaEffect.ShieldBoosting) && Equals(n.Arguments[3], false)
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Null(saved.Ships.Find(pilot.ShipId));
    }
}
