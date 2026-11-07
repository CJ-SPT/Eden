using Eden.Data.Catalog;
using Eden.Data.Combat;
using Eden.Data.Fittings;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Propulsion;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Propulsion;

public sealed class AfterburnerTierTests
{
    private const int TechTwo = 438;
    private const int GistiiAType = 18670;

    private static IPilotPropulsion Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotPropulsion>();

    private static Fixture WithTiers(params int[] types)
    {
        Fixture fixture = new();
        var selected = types.Concat(Enum.GetValues<NavigationSkill>().Select(skill => (int)skill)).ToHashSet();
        PassiveCatalogFixture.LoadActiveTiers(fixture.CatalogPath, selected);
        return fixture;
    }

    private static void Train(Fixture f)
    {
        f.Train((int)NavigationSkill.Navigation, 3);
        f.Train((int)NavigationSkill.Afterburner, 4);
        f.Train((int)NavigationSkill.FuelConservation, 3);
        f.Train((int)NavigationSkill.AccelerationControl, 2);
        f.Train((int)NavigationSkill.HighSpeedManeuvering, 5);
    }

    private static PilotCallContext Client(Fixture f)
    {
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(f.Db.Store, selected, f.Clock, new(), f.Db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    [Fact]
    public void AllSixtyFourUnrestrictedStaticProfilesUseTheirOwnValidMassAndThrust()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadActiveTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog
            .Set<ItemType>()
            .Where(type => type.GroupId == PropulsionDefinitions.AfterburnerGroup)
            .AsEnumerable()
            .Where(type => FittingCatalog.Effects(catalog, type.Id).Contains(DogmaEffect.Afterburner))
            .ToArray();
        var supported = types.Where(type => AfterburnerCatalog.Supported(catalog, type.Id, type.GroupId)).ToArray();
        Assert.Equal(74, types.Length);
        Assert.Equal(64, supported.Length);
        var basis = f.Db.Store.FindPilot(1000001)!.Flight;
        Assert.All(
            supported,
            type =>
            {
                Assert.Equal(ModuleRack.Medium, FittingCatalog.Definition(catalog, type.Id)!.Rack);
                var attributes = PilotFittingStore.ReadAttributes(catalog, type.Id);
                PropulsionModule module = new(
                    type.Id,
                    type.Id,
                    attributes[DogmaAttributeId.Duration],
                    attributes[DogmaAttributeId.CapacitorNeed],
                    attributes[DogmaAttributeId.SpeedFactor],
                    attributes[DogmaAttributeId.SpeedBoostFactor],
                    attributes[DogmaAttributeId.MassAddition]
                );
                var boosted = module.Apply(basis);
                Assert.True(boosted.MaxSpeed > basis.MaxSpeed);
                Assert.True(boosted.Mass > basis.Mass);
            }
        );
        Assert.All(types.Except(supported), type => Assert.Null(FittingCatalog.Definition(catalog, type.Id)));
        var tiers = supported
            .Select(type => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(type.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.All(new long[] { 1, 2, 3, 4, 5, 6 }, tier => Assert.Contains(tier, tiers));
    }

    [Theory]
    [InlineData(TechTwo, 135, 22)]
    [InlineData(14110, 145, 18)]
    [InlineData(GistiiAType, 160, 18)]
    [InlineData(21470, 135, 15)]
    [InlineData(84966, 165, 18)]
    [InlineData(85025, 170, 24)]
    [InlineData(85031, 147.5, 18)]
    public void HigherTiersPayTheirOwnCostAndApplyNavigationSkillsOnceWithoutMicrowarpdrivePenalties(int type, double factor, double cost)
    {
        using Fixture f = WithTiers(type);
        long module = f.Fit(type);
        Train(f);
        var before = f.Fitting.ShipAttributes(f.PilotId);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        var flight = f.Db.Store.FindPilot(1000001)!.Flight;
        var paid = Store(f).Activate(f.PilotId, module, f.Clock.Now, DogmaEffect.Afterburner);
        Assert.Equal(8000, paid.Module.DurationMilliseconds);
        Assert.Equal(cost * .6 * .7, paid.Module.CapacitorNeed, 8);
        Assert.Equal(factor * 1.1, paid.Module.SpeedFactor, 8);
        Assert.Equal(before[DogmaAttributeId.CapacitorCapacity] - cost * .6 * .7, paid.Capacitor, 8);
        Assert.Equal(before[DogmaAttributeId.CapacitorCapacity], f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CapacitorCapacity]);
        Assert.Equal(before[DogmaAttributeId.SignatureRadius], paid.SignatureRadius);
        var boosted = paid.Module.Apply(flight);
        Assert.Equal(flight.Mass + 500000, boosted.Mass);
        Assert.Equal(flight.MaxSpeed * (1 + factor * 1.1 / 100 * 1500000 / boosted.Mass), boosted.MaxSpeed, 3);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(
            brain.ShipEffects,
            effect => effect.Skills.Contains((int)NavigationSkill.AccelerationControl) && effect.Value == 10
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + 8000 * TimeSpan.TicksPerMillisecond, saved.Set<ModuleCooldown>().Single().ReadyAt);
    }

    [Fact]
    public void HigherTierRepeatsKeepOneFlightBonusAndFinishThePaidManualStop()
    {
        using Fixture f = WithTiers(GistiiAType);
        long module = f.Fit(GistiiAType);
        Train(f);
        using var context = Client(f);
        using PropulsionHandler handler = new(f.Clock, Store(f));
        Assert.Null(handler.CallWire(context, "dogmaIM", "Activate", [module, PropulsionWireDefinitions.EffectName, null, 100L]));
        float speed = context.Pilot.Flight.MaxSpeed;
        context.Notifications.Clear();
        for (int cycle = 0; cycle < 2; cycle++)
        {
            f.Clock.Milliseconds += 8000;
            handler.Advance(context);
            Assert.Equal(speed, context.Pilot.Flight.MaxSpeed);
            Assert.Empty(context.Notifications.OfType<ShipEffectChanged>());
        }
        Assert.Null(handler.CallWire(context, "dogmaIM", "Deactivate", [module, PropulsionWireDefinitions.EffectName]));
        f.Clock.Milliseconds += 7999;
        handler.Advance(context);
        Assert.NotNull(handler.ActiveEffect(context.Pilot, module));
        f.Clock.Milliseconds++;
        handler.Advance(context);
        Assert.Null(handler.ActiveEffect(context.Pilot, module));
        Assert.Equal(339.25, context.Pilot.Flight.MaxSpeed, 2);
        Assert.False(Assert.Single(context.Notifications.OfType<ShipEffectChanged>()).Start);
    }

    [Fact]
    public void TechTwoRequiresAfterburnerFourAndRestartCannotBypassItsPaidDeadline()
    {
        using Fixture f = WithTiers(TechTwo);
        long module = f.Fit(TechTwo);
        f.Train((int)NavigationSkill.Afterburner, 3);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        f.Train((int)NavigationSkill.Afterburner, 4);
        var paid = Store(f).Activate(f.PilotId, module, f.Clock.Now);
        WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Single().Charge, 8);
        Assert.Empty(saved.Set<ActiveFittedModule>());
        Assert.Equal(f.Clock.Now + 8000 * TimeSpan.TicksPerMillisecond, saved.Set<ModuleCooldown>().Single().ReadyAt);
    }

    [Theory]
    [InlineData("effect")]
    [InlineData("mutation")]
    [InlineData("restriction")]
    public void UnsupportedStoredProfilesCannotSpendCapacitorOrApplyFlightBonuses(string reason)
    {
        using Fixture f = WithTiers(TechTwo);
        long module = f.Fit(TechTwo);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            switch (reason)
            {
                case "effect":
                    string addedEffect = """{"effectID":4}""";
                    catalog.Database.ExecuteSql(
                        $"""
                        UPDATE SourceRecord SET Data=json_insert(Data,'$.dogmaEffects[#]',json({addedEffect}))
                        WHERE Dataset='client/typedogma' AND Key='438'
                        """
                    );
                    break;
                case "mutation":
                    catalog.Database.ExecuteSqlRaw(
                        "UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key='438'"
                    );
                    break;
                case "restriction":
                    catalog.Database.ExecuteSql($"INSERT INTO TypeAttribute VALUES ({TechTwo},{(int)DogmaAttributeId.CanFitShipGroup1},1)");
                    break;
            }
            Assert.Null(FittingCatalog.Definition(catalog, TechTwo));
        }
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => Store(f).Activate(f.PilotId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<ShipEnergy>());
        Assert.Empty(saved.Set<ModuleCooldown>());
        Assert.Empty(saved.Set<ActiveFittedModule>());
    }
}
