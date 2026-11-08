using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Defenses;

public sealed partial class SignatureSuppressionOperationTests
{
    private const int Basic = 4409;
    private const int Raven = 638;
    private const double SecondPenalty = .8691199808003975;

    private static Fixture Prepare(int masking = 0)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadSignatureSuppression(f.CatalogPath);
        Eden.Protocol.Tests.Persistence.SmallHullTests.Board(f, Raven);
        f.Train(SignatureSuppressionRules.MaskingSkill, masking);
        return f;
    }

    private static IPilotSignatureSuppression Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotSignatureSuppression>();

    private static void Space(Fixture f, double capacitor = 5000)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(f.ShipId, new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z));
        using WorldDbContext world = new(f.Db.Path);
        var energy = world.Set<ShipEnergy>().Find(f.ShipId) ?? world.Add(new ShipEnergy { ShipId = f.ShipId }).Entity;
        energy.Charge = capacitor;
        energy.UpdatedAt = f.Clock.Now;
        world.SaveChanges();
    }

    [Theory]
    [InlineData(Basic)]
    [InlineData(83643)]
    [InlineData(83644)]
    [InlineData(83660)]
    [InlineData(83661)]
    public void AllProfilesFitRealBudgetsPayOnceAndExpireBeforeObserverReads(int type)
    {
        using Fixture f = Prepare(masking: 3);
        long module = f.Fit(type);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        using CatalogDbContext catalog = new(f.CatalogPath);
        var raw = catalog.FittingAttributes(type);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        Assert.True(before[DogmaAttributeId.CpuLoad] <= before[DogmaAttributeId.CpuOutput]);
        Assert.True(before[DogmaAttributeId.PowerLoad] <= before[DogmaAttributeId.PowerOutput]);
        double passive = raw[DogmaAttributeId.SignatureSuppressionPassiveMultiplier];
        double active = raw[DogmaAttributeId.SignatureSuppressionActiveMultiplier];
        double baseline = catalog.FittingAttributes(Raven)[DogmaAttributeId.SignatureRadius];
        Assert.Equal(baseline * passive, before[DogmaAttributeId.SignatureRadius], 8);
        var result = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        Assert.Equal(raw[DogmaAttributeId.SignatureSuppressionDuration] * 1.15, result.Module.DurationMilliseconds, 7);
        Assert.Equal(5000 - raw[DogmaAttributeId.CapacitorNeed], result.Capacitor, 8);
        double signature = baseline * active * (1 + (passive - 1) * SecondPenalty);
        Assert.Equal(signature, result.ShipAttributes[DogmaAttributeId.SignatureRadius], 8);
        Assert.Equal(signature, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(result.Schedule.ExpiresAt - f.Clock.Now - 1));
        Assert.Equal(signature, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(before[DogmaAttributeId.SignatureRadius], f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        Assert.False(Store(f).Active(f.PilotId, f.ShipId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<ActiveFittedModule>().Find(module));
        Assert.Equal(result.Schedule.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
    }

    [Theory]
    [InlineData("capacitor")]
    [InlineData("offline")]
    [InlineData("owner")]
    [InlineData("docked")]
    [InlineData("destroyed")]
    [InlineData("hull")]
    [InlineData("slot")]
    [InlineData("skills")]
    public void InvalidActivationCannotSpendCapacitorOrCreateADefenseDeadline(string rejection)
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f, rejection == "capacitor" ? 0 : 5000);
        if (rejection == "docked")
        {
            f.Db.Store.SetPosition(f.ShipId, null);
        }
        if (rejection == "skills")
        {
            f.Train(3427, 0);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            if (rejection == "offline")
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
            }
            if (rejection == "owner")
            {
                world.Set<OwnedItem>().Find(module)!.OwnerId = world.Pilots.Single(p => p.Id != f.PilotId).Id;
            }
            if (rejection == "destroyed")
            {
                world.Add(
                    new ShipHealthState
                    {
                        ShipId = f.ShipId,
                        StructureDamage = 1000000,
                        UpdatedAt = f.Clock.Now,
                    }
                );
            }
            if (rejection == "hull")
            {
                world.Ships.Find(f.ShipId)!.TypeId = 601;
            }
            if (rejection == "slot")
            {
                world.Set<OwnedItem>().Find(module)!.FlagId = (int)InventoryFlag.HighSlot0;
            }
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(rejection == "capacitor" ? 0 : 5000, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Empty(verify.Set<ActiveFittedModule>());
        Assert.Null(verify.Set<ModuleCooldown>().Find(module));
    }

    [Fact]
    public void FittingRejectsWrongHullAndDuplicatesWithoutDroppingExistingEquipment()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Assert.Throws<InvalidDataException>(() => f.Fit(83643));
        Assert.Contains(f.Inventory.List(f.PilotId, f.ShipId), i => i.Id == module && i.Online);
        using Fixture corvette = new();
        PassiveCatalogFixture.LoadSignatureSuppression(corvette.CatalogPath);
        Assert.Throws<InvalidDataException>(() => corvette.Fit(Basic));
        Assert.DoesNotContain(corvette.Inventory.List(corvette.PilotId, corvette.ShipId), i => i.TypeId == Basic);
    }

    [Fact]
    public async Task CheckpointAndRestartRetainPaymentAndCooldownButClearTheActiveBonus()
    {
        using Fixture f = Prepare(masking: 5);
        long module = f.Fit(Basic);
        Space(f);
        double passive = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius];
        SignatureSuppressionActivation result;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            result = Store(f).Activate(f.PilotId, module, f.Clock.Now);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Equal(4340, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(result.Schedule.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Equal(passive, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
    }

    [Fact]
    public void WarmOwnerAndObserverReadsHaveNoSqlUntilTheExactPaidExpiry()
    {
        using Fixture f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        double passive = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius];
        var result = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        long ship = f.ShipId;
        _ = f.Fitting.ShipAttributes(f.PilotId);
        Assert.True(Store(f).Active(f.PilotId, f.ShipId, module, f.Clock.Now));
        using (var metrics = SessionPerformance.Begin("warm signature suppression", _ => { }, force: true))
        {
            for (int frame = 0; frame < 12; frame++)
            {
                f.Clock.Milliseconds += 50;
                Assert.True(Store(f).Active(f.PilotId, ship, module, f.Clock.Now));
                _ = f.Fitting.ShipAttributes(f.PilotId);
            }
            Assert.Equal(0, metrics!.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(result.Schedule.ExpiresAt - f.Clock.Now));
        Assert.Equal(passive, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.SignatureRadius], 8);
        _ = f.Fitting.ShipAttributes(f.PilotId);
        Assert.False(Store(f).Active(f.PilotId, ship, module, f.Clock.Now));
        using var expired = SessionPerformance.Begin("warm expired suppression", _ => { }, force: true);
        Assert.False(Store(f).Active(f.PilotId, ship, module, f.Clock.Now));
        _ = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(0, expired!.Commands);
        Assert.Equal(0, expired.Connections);
    }
}
