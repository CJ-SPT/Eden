using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Repair;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Ammunition;

public sealed class CapacitorBoosterTierTests
{
    private const int SmallTechTwo = 3568;
    private const int ChargeTwentyFive = 263;

    private static Fixture WithTiers(params int[] types)
    {
        Fixture fixture = new();
        PassiveCatalogFixture.LoadActiveTiers(fixture.CatalogPath, types.ToHashSet());
        return fixture;
    }

    private static IPilotAmmunition Ammo(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    private static IPilotCapacitorBoosting Boosting(Fixture f) => f.Db.Services.GetRequiredService<IPilotCapacitorBoosting>();

    private static long Prepare(Fixture f, int type, int chargeType, int quantity)
    {
        long module = f.Fit(type);
        long stack = f.Grant(chargeType, quantity);
        Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 10000;
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        using WorldDbContext saved = new(f.Db.Path);
        saved
            .Set<ShipEnergy>()
            .Add(
                new()
                {
                    ShipId = f.ShipId,
                    Charge = 25,
                    UpdatedAt = f.Clock.Now,
                }
            );
        saved.SaveChanges();
        return module;
    }

    [Fact]
    public void AllFortyThreePublishedStaticProfilesUseTheirCompleteCatalogActivation()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadActiveTiers(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(type => type.GroupId == CapacitorBoosterDefinitions.Group).ToArray();
        Assert.Equal(43, types.Length);
        Assert.All(
            types,
            type =>
            {
                Assert.True(CapacitorBoosterCatalog.Supported(catalog, type.Id, type.GroupId));
                Assert.Equal(ModuleRack.Medium, FittingCatalog.Definition(catalog, type.Id)!.Rack);
            }
        );
        var tiers = types
            .Select(type => catalog.Set<SourceRecord>().Find("client/types", PassiveCatalog.Key(type.Id))!.Read<ClientTypes>().MetaGroupID)
            .ToHashSet();
        Assert.All(new long[] { 1, 2, 3, 4, 5 }, tier => Assert.Contains(tier, tiers));
    }

    [Theory]
    [InlineData(SmallTechTwo, 15, 12000)]
    [InlineData(20555, 12, 13500)]
    [InlineData(14184, 12, 11250)]
    [InlineData(23807, 15, 13750)]
    [InlineData(85027, 12, 9000)]
    public void HigherTiersRetainTheirMagazineVolumeAndDistinctPaidCycle(int type, int capacity, double duration)
    {
        using Fixture f = WithTiers(type);
        long module = Prepare(f, type, ChargeTwentyFive, 30);
        Assert.Equal(capacity, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        var injection = Boosting(f).Inject(f.PilotId, module, f.Clock.Now);
        Assert.Equal(25, injection.Module.Amount);
        Assert.Equal(duration, injection.Module.DurationMilliseconds);
        Assert.Equal(capacity - 1, injection.Magazine.Quantity);
        Assert.Equal(50, injection.Capacitor, 8);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Inject(f.PilotId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(f.Clock.Now + (long)(duration * TimeSpan.TicksPerMillisecond), saved.Set<ModuleCooldown>().Single().ReadyAt);
        Assert.Equal(29, saved.Set<OwnedItem>().Where(item => item.TypeId == ChargeTwentyFive).Sum(item => item.Quantity));
    }

    [Fact]
    public void TechTwoRequiresCapacitorSystemsOperationThreeBeforePayingOrConsumingCharges()
    {
        using Fixture f = WithTiers(SmallTechTwo);
        long module = Prepare(f, SmallTechTwo, CapacitorBoosterDefinitions.ChargeOneHundred, 9);
        f.Train((int)FittingSkill.CapacitorSystemsOperation, 1);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Inject(f.PilotId, module, f.Clock.Now));
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(25, saved.Set<ShipEnergy>().Single().Charge);
            Assert.Equal(3, saved.Set<OwnedItem>().Single(item => item.LocationId == module).Quantity);
            Assert.Equal(ModuleCooldownKind.Reload, saved.Set<ModuleCooldown>().Single().Kind);
        }
        f.Train((int)FittingSkill.CapacitorSystemsOperation, 3);
        Assert.Equal(125, Boosting(f).Inject(f.PilotId, module, f.Clock.Now).Capacitor, 8);
    }

    [Fact]
    public void HigherTierRestartRetainsThePaidCycleAndRealMagazineWithoutOfflineInjection()
    {
        using Fixture f = WithTiers(SmallTechTwo);
        long module = Prepare(f, SmallTechTwo, CapacitorBoosterDefinitions.ChargeOneHundred, 9);
        Boosting(f).Inject(f.PilotId, module, f.Clock.Now);
        long deadline = f.Clock.Now + 12000 * TimeSpan.TicksPerMillisecond;
        WorldInitialization.Open(f.Db.Path);
        Assert.Throws<InvalidDataException>(() => Boosting(f).Inject(f.PilotId, module, f.Clock.Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(deadline, saved.Set<ModuleCooldown>().Single().ReadyAt);
        Assert.Equal(125, saved.Set<ShipEnergy>().Single().Charge, 8);
        Assert.Equal(2, saved.Set<OwnedItem>().Single(item => item.LocationId == module).Quantity);
        Assert.Equal(
            8,
            saved.Set<OwnedItem>().Where(item => item.TypeId == CapacitorBoosterDefinitions.ChargeOneHundred).Sum(i => i.Quantity)
        );
    }

    [Theory]
    [InlineData("publication")]
    [InlineData("mutation")]
    [InlineData("restriction")]
    [InlineData("effect")]
    public void UnsupportedProfilesAreRejectedBeforeFittingWithoutDroppingAnyEffect(string reason)
    {
        using Fixture f = WithTiers(SmallTechTwo);
        f.Train((int)FittingSkill.CapacitorSystemsOperation, 3);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            switch (reason)
            {
                case "publication":
                    catalog.Database.ExecuteSqlRaw(
                        "UPDATE SourceRecord SET Data=json_set(Data,'$.published',0) WHERE Dataset='client/types' AND Key='3568'"
                    );
                    break;
                case "mutation":
                    catalog.Database.ExecuteSqlRaw(
                        "UPDATE SourceRecord SET Data=json_set(Data,'$.metaGroupID',15) WHERE Dataset='client/types' AND Key='3568'"
                    );
                    break;
                case "restriction":
                    catalog.Database.ExecuteSql(
                        $"INSERT INTO TypeAttribute VALUES ({SmallTechTwo},{(int)DogmaAttributeId.CanFitShipGroup1},1)"
                    );
                    break;
                case "effect":
                    string addedEffect = """{"effectID":6730}""";
                    catalog.Database.ExecuteSql(
                        $"""
                        UPDATE SourceRecord SET Data=json_insert(Data,'$.dogmaEffects[#]',json({addedEffect}))
                        WHERE Dataset='client/typedogma' AND Key='3568'
                        """
                    );
                    break;
            }
            Assert.Null(FittingCatalog.Definition(catalog, SmallTechTwo));
        }
        long item = f.Grant(SmallTechTwo);
        Assert.Throws<InvalidDataException>(() => f.Fitting.Fit(f.PilotId, [item], f.StationId, (int)InventoryFlag.AutoFit));
        Assert.Contains(f.Inventory.List(f.PilotId, f.StationId), saved => saved.Id == item && !saved.Singleton && saved.Quantity == 1);
    }
}
