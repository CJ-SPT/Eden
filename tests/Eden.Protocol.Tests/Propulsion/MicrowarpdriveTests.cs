using Eden.Data.Catalog;
using Eden.Data.Combat;
using Eden.Data.Fittings;
using Eden.Data.Inventory;
using Eden.Data.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Propulsion;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Propulsion;

public sealed class MicrowarpdriveTests
{
    private static IPilotPropulsion Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotPropulsion>();

    private static long Fit(Fixture f, bool online = true)
    {
        f.Train((int)NavigationSkill.Navigation, 3);
        long module = f.Fit((int)PropulsionModuleType.FiveMnMicrowarpdriveI, online);
        f.Train((int)NavigationSkill.HighSpeedManeuvering, 3);
        return module;
    }

    private static PilotCallContext Client(Fixture f, SharedSpace? space = null, SpacePosition? position = null)
    {
        f.Db.Store.SetPosition(f.ShipId, position ?? SpacePosition.Zero);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(f.Db.Store, selected, f.Clock, space ?? new(), f.Db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    [Fact]
    public void AllSixtyTwoUnrestrictedStaticProfilesAreSupportedAcrossTiersWithoutEnablingMutationsOrManufacturing()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => t.GroupId == PropulsionDefinitions.AfterburnerGroup).ToArray();
        var supported = types.Where(t => MicrowarpdriveCatalog.Supported(catalog, t.Id, t.GroupId)).ToArray();
        Assert.Equal(62, supported.Length);
        Assert.All(supported, type => Assert.Equal(ModuleRack.Medium, FittingCatalog.Definition(catalog, type.Id)!.Rack));
        Assert.NotNull(FittingCatalog.Definition(catalog, 440));
        Assert.Null(FittingCatalog.Definition(catalog, 47740)); // Abyssal modules need per-item mutated attributes.
        Assert.Null(FittingCatalog.Definition(catalog, 41249)); // Ship-restricted capital modules are deferred.
        Assert.NotNull(FittingCatalog.Definition(catalog, 439));
        var products = f.Db.Services.GetRequiredService<Eden.Gameplay.Industry.IPilotIndustry>().ProductTypes();
        Assert.DoesNotContain((int)PropulsionModuleType.FiveMnMicrowarpdriveI, products);
    }

    [Fact]
    public void OnlineCapacityPenaltyPreservesCapacitorFractionAndDoesNotAddAnInactiveSignatureBonus()
    {
        using Fixture f = new();
        long module = Fit(f, online: false);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Add(
                new ShipEnergy
                {
                    ShipId = f.ShipId,
                    Charge = before[DogmaAttributeId.CapacitorCapacity] / 2,
                    UpdatedAt = f.Clock.Now,
                }
            );
            saved.SaveChanges();
        }
        f.Fitting.SetOnline(f.PilotId, f.ShipId, module, true);
        var online = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(before[DogmaAttributeId.CapacitorCapacity] * .75, online[DogmaAttributeId.CapacitorCapacity], 8);
        Assert.Equal(before[DogmaAttributeId.SignatureRadius], online[DogmaAttributeId.SignatureRadius]);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(online[DogmaAttributeId.CapacitorCapacity] / 2, saved.Set<ShipEnergy>().Single().Charge, 8);
        }
        f.Fitting.SetOnline(f.PilotId, f.ShipId, module, false);
        Assert.Equal(
            before[DogmaAttributeId.CapacitorCapacity],
            f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CapacitorCapacity],
            8
        );
    }

    [Fact]
    public void HighSpeedManeuveringAppliesOnceAndAfterburnerSkillsDoNotChangeMicrowarpdriveCostOrDuration()
    {
        using Fixture f = new();
        long module = Fit(f);
        f.Train((int)NavigationSkill.Afterburner, 5);
        f.Train((int)NavigationSkill.FuelConservation, 5);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var attributes = Store(f).ModuleAttributes(f.PilotId, item);
        Assert.Equal(38.25, attributes[DogmaAttributeId.CapacitorNeed], 8);
        Assert.Equal(10000, attributes[DogmaAttributeId.Duration]);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(brain.ShipEffects, e => e.Skills.Contains((int)NavigationSkill.HighSpeedManeuvering) && e.Value == -15);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var flight = f.Db.Store.FindPilot(1000001)!.Flight;
        var paid = Store(f).Activate(f.PilotId, module, f.Clock.Now, DogmaEffect.Microwarpdrive);
        Assert.Equal(DogmaEffect.Microwarpdrive, paid.Module.Effect);
        Assert.Equal(500, paid.Module.SpeedFactor);
        var boosted = paid.Module.Apply(flight);
        Assert.Equal(1663000, boosted.Mass);
        Assert.InRange(boosted.MaxSpeed, 1869, 1870);
        Assert.Equal(312, paid.SignatureRadius, 8);
    }

    [Fact]
    public void SignatureBloomAffectsTheWholeFittedSignatureAndReturnsWhenTheModuleStops()
    {
        using Fixture f = new();
        long module = Fit(f);
        _ = f.Fit(377);
        double before = f.Fitting.SignatureRadius(f.ShipId, 601);
        Assert.Equal(54, before, 8);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        _ = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        Assert.Equal(324, f.Fitting.SignatureRadius(f.ShipId, 601), 8);
        Assert.Equal(54, Store(f).Stop(f.PilotId, f.ShipId, module), 8);
    }

    [Theory]
    [InlineData(440, .8, 475, 510, 34)] // Tech II.
    [InlineData(14122, .9, 450, 512, 38.25)] // Domination.
    [InlineData(19325, .97, 410, 518, 34)] // Coreli A-Type.
    [InlineData(84962, .99, 390, 520, 34)] // Asine's Modified.
    public void HigherTierModulesUseTheirOwnCapacitySignatureSpeedAndPayment(
        int type,
        double capacityMultiplier,
        double signatureBonus,
        double speedFactor,
        double cost
    )
    {
        using Fixture f = new();
        double before = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CapacitorCapacity];
        long module = f.Fit(type);
        f.Train((int)NavigationSkill.HighSpeedManeuvering, 3);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Assert.Equal(before * capacityMultiplier, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CapacitorCapacity], 8);
        var paid = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        Assert.Equal(cost, paid.Module.CapacitorNeed, 8);
        Assert.Equal(speedFactor, paid.Module.SpeedFactor);
        Assert.Equal(52 * (1 + signatureBonus / 100), paid.SignatureRadius, 8);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(before * capacityMultiplier - cost, saved.Set<ShipEnergy>().Single().Charge, 8);
    }

    [Fact]
    public void AfterburnerAndMicrowarpdriveShareTheSingleActivePropulsionLimit()
    {
        using Fixture f = new();
        f.Train((int)FittingSkill.PowerGridManagement, 3);
        long module = Fit(f);
        long afterburner = f.Fit(439);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        _ = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, afterburner, f.Clock.Now));
    }

    [Fact]
    public void RepeatsKeepOneSpeedAndSignatureBonusUntilThePaidManualStop()
    {
        using Fixture f = new();
        long module = Fit(f);
        using var context = Client(f);
        using PropulsionHandler handler = new(f.Clock, Store(f));
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "dogmaIM", "Activate", [module, PropulsionWireDefinitions.MicrowarpdriveEffectName, null, 100L])
        );
        float speed = context.Pilot.Flight.MaxSpeed;
        context.Notifications.Clear();
        f.Clock.Milliseconds += 10000;
        handler.Advance(context);
        Assert.Equal(speed, context.Pilot.Flight.MaxSpeed);
        Assert.Equal(312, Store(f).SignatureRadius(f.PilotId), 8);
        Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        _ = handler.Call(context, "dogmaIM", "Deactivate", [module, PropulsionWireDefinitions.MicrowarpdriveEffectName]);
        f.Clock.Milliseconds += 9999;
        handler.Advance(context);
        Assert.NotNull(handler.ActiveEffect(context.Pilot, module));
        f.Clock.Milliseconds++;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.Equal(52, Store(f).SignatureRadius(f.PilotId), 8);
        Assert.Equal(339.25, context.Pilot.Flight.MaxSpeed, 2);
        Assert.Single(context.Notifications.OfType<ShipEffectChanged>());
    }

    [Fact]
    public void FailedRepeatAndScopeDisposalNeverLeaveAnOrphanedSignatureBonus()
    {
        using Fixture f = new();
        long module = Fit(f);
        using var context = Client(f);
        using PropulsionHandler handler = new(f.Clock, Store(f));
        _ = handler.Call(context, "dogmaIM", "Activate", [module, PropulsionWireDefinitions.MicrowarpdriveEffectName, null, 100L]);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var energy = saved.Set<ShipEnergy>().Single();
            energy.Charge = 0;
            energy.UpdatedAt = f.Clock.Now + 10000 * TimeSpan.TicksPerMillisecond;
            saved.SaveChanges();
        }
        f.Clock.Milliseconds += 10000;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.Equal(52, Store(f).SignatureRadius(f.PilotId), 8);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            saved.Set<ShipEnergy>().Single().Charge = 100;
            saved.SaveChanges();
        }
        _ = handler.Call(context, "dogmaIM", "Activate", [module, PropulsionWireDefinitions.MicrowarpdriveEffectName, null, 100L]);
        handler.Dispose();
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<ActiveFittedModule>());
        Assert.True(check.Set<ModuleCooldown>().Single().ReadyAt > f.Clock.Now);
    }

    [Fact]
    public void WarpRejectsNewActivationAndStopsRepeatingAfterItsPaidCycle()
    {
        using Fixture f = new();
        long module = Fit(f);
        // Start clear of the synthetic moon at the catalog origin so alignment
        // can finish and this checks actual warp, rather than its initial order.
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition station = new(pilot.X, pilot.Y, pilot.Z);
        using var context = Client(f, position: station + new SpacePosition(1000000, 0, 0));
        using PropulsionHandler handler = new(f.Clock, Store(f));
        _ = handler.Call(context, "dogmaIM", "Activate", [module, PropulsionWireDefinitions.MicrowarpdriveEffectName, null, 100L]);
        Assert.Null(context.Flight.CallWire("beyonce", "CmdWarpToStuff", ["item", (long)context.Pilot.StationId]));
        f.Clock.Milliseconds += 15000;
        context.Flight.Checkpoint();
        Assert.True(context.Flight.InWarp);
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, "dogmaIM", "Activate", [module, PropulsionWireDefinitions.MicrowarpdriveEffectName, null, 0L])
        );
        Assert.Equal(52, Store(f).SignatureRadius(f.PilotId), 8);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("docked")]
    [InlineData("foreign")]
    [InlineData("skill")]
    [InlineData("effect")]
    public void InvalidStartsDoNotPayOrApplyAnyActiveModifier(string reason)
    {
        using Fixture f = new();
        long module = Fit(f);
        f.Db.Store.SetPosition(f.ShipId, reason == "docked" ? null : SpacePosition.Zero);
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var item = saved.Set<OwnedItem>().Find(module)!;
            if (reason == "offline")
            {
                item.Online = false;
            }
            if (reason == "foreign")
            {
                item.OwnerId = 90000002;
            }
            if (reason == "skill")
            {
                saved.Set<PilotSkill>().Find(f.PilotId, (int)NavigationSkill.HighSpeedManeuvering)!.Points = 0;
            }
            saved.SaveChanges();
        }
        double beforeCharge;
        long beforeUpdated;
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var energy = saved.Set<ShipEnergy>().Single();
            beforeCharge = energy.Charge;
            beforeUpdated = energy.UpdatedAt;
        }
        Assert.Throws<InvalidDataException>(() =>
            Store(f).Activate(f.PilotId, module, f.Clock.Now, reason == "effect" ? DogmaEffect.Afterburner : DogmaEffect.Microwarpdrive)
        );
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<ActiveFittedModule>());
        Assert.Empty(check.Set<ModuleCooldown>());
        var after = check.Set<ShipEnergy>().Single();
        Assert.Equal(beforeCharge, after.Charge);
        Assert.Equal(beforeUpdated, after.UpdatedAt);
    }

    [Fact]
    public void VersionThirtyMigrationAndRestartClearLiveMarkersButRetainPaidResources()
    {
        using Fixture f = new();
        long module = Fit(f);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var paid = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        long deadline;
        using (WorldDbContext saved = new(f.Db.Path))
        {
            deadline = saved.Set<ModuleCooldown>().Single().ReadyAt;
            saved.Database.ExecuteSqlRaw(
                TestWorld.DropSafetySchema
                    + """
                    ALTER TABLE WorldArmorRepair DROP COLUMN Layer;
                    ALTER TABLE WorldItem DROP COLUMN CrystalDamage;
                    DROP INDEX IX_WorldActiveModule_ShipId;
                    ALTER TABLE WorldActiveModule RENAME TO WorldActiveHardener;
                    CREATE INDEX IX_WorldActiveHardener_ShipId ON WorldActiveHardener(ShipId);
                    PRAGMA user_version=30;
                    """
            );
        }
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(52, Store(f).SignatureRadius(f.PilotId), 8);
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<ActiveFittedModule>());
        Assert.Equal(deadline, check.Set<ModuleCooldown>().Single().ReadyAt);
        Assert.Equal(paid.Capacitor, check.Set<ShipEnergy>().Single().Charge, 8);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void NativePrimeAndObserverReplayUseTheMicrowarpdriveEffect()
    {
        using Fixture f = new();
        long module = Fit(f);
        SharedSpace space = new();
        using var context = Client(f, space);
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, new(100, 0, 0));
        using FlightSession observer = new(f.Db.Store, f.Db.Store.FindPilot(1000002)!, f.Clock, space, f.Db.Objects);
        observer.Snapshot();
        observer.Updates.Clear();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Null(
            router.CallWire(
                context,
                "dogmaIM",
                "Activate",
                new object?[] { module, PropulsionWireDefinitions.MicrowarpdriveEffectName, null, 0L },
                _ => { }
            )
        );
        var effect = scope.ServiceProvider.GetRequiredService<PropulsionHandler>().ActiveEffect(context.Pilot, module);
        Assert.Equal(DogmaEffect.Microwarpdrive, effect!.Effect);
        observer.RefreshPeers();
        var actions = observer.WireUpdates().Cast<object?[]>().Select(u => (object?[])u[1]!).ToArray();
        Assert.Contains(
            actions,
            a => Equals(a[0], "OnSpecialFX") && Equals(((object?[])a[1]!)[5], PropulsionWireDefinitions.MicrowarpdriveGraphic)
        );
        var info = Assert.IsType<PilotCallResult.Success>(router.Call(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { }));
        Assert.NotEmpty(BlueMarshal.Encode(info.Reply));
    }
}
