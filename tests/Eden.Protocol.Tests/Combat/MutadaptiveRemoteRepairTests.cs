using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Repair;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class MutadaptiveRemoteRepairTests
{
    private const int Rodiva = 49712;
    private const int PrecursorCruiser = 47868;
    private const int Repairer = 49770;
    private const DogmaEffect Effect = DogmaEffect.MutadaptiveRemoteArmorRepair;

    private static Fixture Prepare(bool board = true, bool thermal = false)
    {
        Fixture f = new();
        if (thermal)
        {
            PassiveCatalogFixture.LoadOverload(f.CatalogPath);
        }
        PassiveCatalogFixture.LoadMutadaptiveRemoteRepairs(f.CatalogPath);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":0}}')"
            );
            // Only the recipient's armor is enlarged to observe a full ramp.
            // The Rodiva keeps its real slot, CPU, power and capacitor budgets.
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)DogmaAttributeId.ArmorHitPoints},1000000)"
            );
        }
        if (board)
        {
            SmallHullTests.Board(f, Rodiva);
        }
        return f;
    }

    private static long Target(Fixture f) => f.Db.Store.FindPilot(1000002)!.ShipId;

    private static IPilotRemoteRepairing Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotRemoteRepairing>();

    private static ShipHealth Health(Fixture f) =>
        f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.Db.Store.FindPilot(1000002)!.PilotId, f.Clock.Now);

    private static void Space(Fixture f)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(Target(f), origin + new SpacePosition(1000, 0, 0));
        using WorldDbContext world = new(f.Db.Path);
        world.Add(
            new ShipHealthState
            {
                ShipId = Target(f),
                ArmorDamage = 900000,
                StructureDamage = 50,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
    }

    private static RemoteRepairActivation Start(Fixture f, long module, bool continuation = false, long? target = null) =>
        Store(f)
            .Start(f.PilotId, module, Effect, target ?? Target(f), new HashSet<long> { target ?? Target(f) }, f.Clock.Now, continuation);

    private static void Finish(Fixture f, RemoteRepairActivation activation)
    {
        f.Clock.Milliseconds += (long)activation.Cycle.Module.DurationMilliseconds;
        Store(f).Complete(f.PilotId, activation.Cycle.Module.ItemId, f.Clock.Now);
    }

    private static void Charge(Fixture f, double amount)
    {
        using WorldDbContext world = new(f.Db.Path);
        var energy = world.Set<ShipEnergy>().Find(f.ShipId) ?? world.Add(new ShipEnergy { ShipId = f.ShipId }).Entity;
        energy.Charge = amount;
        energy.UpdatedAt = f.Clock.Now;
        world.SaveChanges();
    }

    [Theory]
    [InlineData(49770, 392, 465, 10500, .1, 1.5)]
    [InlineData(49771, 448, 465, 11625, .1, 1.5)]
    [InlineData(49772, 448, 465, 10750, .1, 1.5)]
    [InlineData(49773, 512, 513, 12000, .1, 1.5)]
    [InlineData(49774, 512, 513, 12500, .12, 1.8)]
    public void EveryStaticProfileUsesActualRodivaBonusesAndPaidRamping(
        int type,
        double amount,
        double cost,
        double range,
        double step,
        double maximum
    )
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        for (int cycle = 0; cycle < 18; cycle++)
        {
            Charge(f, 2500);
            double before = Health(f).Remaining.Armor;
            var activation = Start(f, module, continuation: cycle > 0);
            double expected = amount * 1.625 * (1 + Math.Min(maximum, cycle * step));
            Assert.Equal(expected, activation.Cycle.Module.Amount, 8);
            Assert.Equal(cost * .75 * .75, activation.Cycle.Module.CapacitorNeed, 8);
            Assert.Equal(range * 6.25, activation.Cycle.Module.Optimal, 8);
            Assert.Equal(0, activation.Cycle.Module.Falloff);
            Assert.Equal(cycle, activation.Cycle.RampCycles);
            Assert.Equal(before, Health(f).Remaining.Armor);
            Store(f).Complete(f.PilotId, module, activation.Cycle.CompletesAt - 1);
            Assert.Equal(before, Health(f).Remaining.Armor);
            Finish(f, activation);
            Assert.Equal(before + expected, Health(f).Remaining.Armor, 7);
            Store(f).Complete(f.PilotId, module, f.Clock.Now);
            Assert.Equal(before + expected, Health(f).Remaining.Armor, 7);
            Assert.Equal(50, Health(f).Defense.Structure - Health(f).Remaining.Structure, 8);
        }
        Store(f).Stop(f.PilotId, module);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<WorldRemoteRepair>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void RodivaUsesEarnedCruiserLevelsAndRemoteArmorSkillExactlyOnce(int level)
    {
        using Fixture f = Prepare();
        long module = f.Fit(Repairer);
        f.Train(PrecursorCruiser, level);
        f.Train((int)RemoteRepairSkill.RemoteArmorRepairSystems, 2);
        Space(f);
        var activation = Start(f, module);
        Assert.Equal(392 * (1 + .125 * level), activation.Cycle.Module.Amount, 8);
        Assert.Equal(465 * (1 - .05 * level) * .9, activation.Cycle.Module.CapacitorNeed, 8);
    }

    [Fact]
    public void RestrictionsAndDuplicateFittingPreserveInventory()
    {
        using Fixture f = Prepare(board: false);
        long module = f.Grant(Repairer);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [module], f.StationId, (int)InventoryFlag.AutoFit));
        SmallHullTests.Board(f, Rodiva);
        f.Fit(Repairer);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [module], f.StationId, (int)InventoryFlag.AutoFit));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(f.StationId, world.Set<OwnedItem>().Find(module)!.LocationId);
        Assert.False(world.Set<OwnedItem>().Find(module)!.Online);
        using CatalogDbContext catalog = new(f.CatalogPath);
        Assert.True(HullCatalog.CanAssemble(catalog, catalog.FittingType(Rodiva)!, (int)InventoryCategory.Ship));
        Assert.False(HullCatalog.CanAssemble(catalog, catalog.FittingType(49713)!, (int)InventoryCategory.Ship));
        Assert.All(
            HullCatalog.Capabilities(catalog, Rodiva).Where(c => c.Capability == HullCapability.RemoteRepair),
            c => Assert.True(c.Operational)
        );
    }

    [Fact]
    public void FreshActivationAndStopResetTheRampAndMissingChainsCannotContinue()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Repairer);
        Space(f);
        Assert.Throws<InvalidDataException>(() => Start(f, module, continuation: true));
        var first = Start(f, module);
        Assert.Throws<InvalidDataException>(() => Start(f, module, continuation: true));
        Finish(f, first);
        var second = Start(f, module, continuation: true);
        Assert.Equal(first.Cycle.Module.Amount * 1.1, second.Cycle.Module.Amount, 8);
        Finish(f, second);
        var fresh = Start(f, module);
        Assert.Equal(first.Cycle.Module.Amount, fresh.Cycle.Module.Amount);
        Store(f).Stop(f.PilotId, module);
        f.Clock.Milliseconds += 6000;
        Assert.Throws<InvalidDataException>(() => Start(f, module, continuation: true));
        Assert.Equal(first.Cycle.Module.Amount, Start(f, module).Cycle.Module.Amount);
    }

    [Theory]
    [InlineData("capacitor")]
    [InlineData("lock")]
    [InlineData("offline")]
    [InlineData("owner")]
    [InlineData("hull")]
    [InlineData("range")]
    [InlineData("skill")]
    public void InvalidActivationPreservesHealthEnergyAndCooldown(string reason)
    {
        using Fixture f = Prepare();
        long module = f.Fit(Repairer);
        Space(f);
        Charge(f, reason == "capacitor" ? 0 : 2000);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(module)!;
            if (reason == "offline")
            {
                item.Online = false;
            }
            if (reason == "owner")
            {
                item.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
            }
            if (reason == "hull")
            {
                world.Ships.Find(f.ShipId)!.TypeId = 601;
            }
            world.SaveChanges();
        }
        if (reason == "skill")
        {
            f.Train((int)RemoteRepairSkill.RemoteArmorRepairSystems, 1);
        }
        if (reason == "range")
        {
            f.Db.Store.SetPosition(Target(f), new SpacePosition(1e15, 0, 0));
        }
        using (WorldDbContext before = new(f.Db.Path))
        {
            double capacitor = before.Set<ShipEnergy>().Find(f.ShipId)!.Charge;
            Assert.Throws<InvalidDataException>(() =>
                Store(f)
                    .Start(
                        f.PilotId,
                        module,
                        Effect,
                        Target(f),
                        reason == "lock" ? new HashSet<long>() : new HashSet<long> { Target(f) },
                        f.Clock.Now
                    )
            );
            using WorldDbContext after = new(f.Db.Path);
            Assert.Equal(capacitor, after.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
            Assert.Null(after.Set<ModuleCooldown>().Find(module));
            Assert.Empty(after.Set<WorldRemoteRepair>());
            Assert.Equal(900000, after.Set<ShipHealthState>().Find(Target(f))!.ArmorDamage);
        }
    }

    [Fact]
    public async Task CheckpointAndRestartKeepPaymentButClearPendingRepairAndRamp()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Repairer);
        Space(f);
        RemoteRepairActivation activation;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var first = Start(f, module);
            Finish(f, first);
            activation = Start(f, module, continuation: true);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<WorldRemoteRepair>());
            Assert.Equal(activation.Capacitor, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 8);
            Assert.Equal(activation.Cycle.CompletesAt, world.Set<ModuleCooldown>().Find(module)!.ReadyAt);
            Assert.Equal(900000 - 392 * 1.625, world.Set<ShipHealthState>().Find(Target(f))!.ArmorDamage, 7);
        }
        Finish(f, activation);
        Assert.Throws<InvalidDataException>(() => Start(f, module, continuation: true));
        Assert.Equal(392 * 1.625, Start(f, module).Cycle.Module.Amount, 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.001)]
    public void CompletionUsesExactSurfaceRangeAndRecipientImpedance(double outside)
    {
        using Fixture f = Prepare();
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            int attribute = (int)DogmaAttributeId.RemoteRepairImpedance;
            string name = nameof(DogmaAttributeId.RemoteRepairImpedance);
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({attribute},{name})");
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)DogmaAttributeId.RemoteRepairImpedance},0.5)"
            );
        }
        long module = f.Fit(Repairer);
        Space(f);
        var activation = Start(f, module);
        var source = f.Db.Store.FindPilot(1000001)!;
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            double radii = catalog.FittingType(Rodiva)!.Radius!.Value + catalog.FittingType(601)!.Radius!.Value;
            f.Db.Store.SetPosition(
                Target(f),
                source.Position! + new SpacePosition(activation.Cycle.Module.Optimal + radii + outside, 0, 0)
            );
        }
        double before = Health(f).Remaining.Armor;
        if (outside == 0)
        {
            Finish(f, activation);
            Assert.Equal(before + 392 * 1.625 * .5, Health(f).Remaining.Armor, 7);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => Finish(f, activation));
            Assert.Equal(before, Health(f).Remaining.Armor);
            using WorldDbContext world = new(f.Db.Path);
            Assert.False(JsonSerializer.Deserialize<RemoteRepairCycle>(world.Set<WorldRemoteRepair>().Find(module)!.CycleJson)!.Settled);
            Assert.Equal(activation.Capacitor, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 8);
        }
    }

    [Fact]
    public void UnsafeAssistanceRollsBackPaymentAndRampState()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Repairer);
        Space(f);
        Charge(f, 2000);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"securityStatus\":1}}' WHERE Dataset='client/systems' AND Key='30000142'"
            );
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            CrimewatchPersistence.State(world, f.Db.Store.FindPilot(1000002)!.PilotId).SuspectUntil =
                f.Clock.Now + TimeSpan.FromMinutes(15).Ticks;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Start(f, module));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(2000, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Empty(verify.Set<WorldRemoteRepair>());
        Assert.Null(verify.Set<ModuleCooldown>().Find(module));
        Assert.Equal(900000, verify.Set<ShipHealthState>().Find(Target(f))!.ArmorDamage);
    }
}
