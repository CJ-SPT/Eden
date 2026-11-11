using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Mining;

public sealed class MiningSkillPointRewardTests
{
    private const long Rock = MiningSiteRules.FirstDepositId;
    private const long InitialPoints = 123;
    private const long InitialRevision = 4;
    private static readonly MiningRolls Rolls = new(.99, .99, .99, .99, .99);

    [Theory]
    [InlineData(39999, 1, 1, 0)]
    [InlineData(40000, 1, 1, 1)]
    [InlineData(20000, 2, 1, 1)]
    [InlineData(20000, 1, 2, 1)]
    [InlineData(15000000, 1, 1, 375)]
    [InlineData(15000000, 3, 2, 2250)]
    [InlineData(15000000, 1, 0, 0)]
    [InlineData(15000000, 0, 1, 0)]
    [InlineData(0, 100, 1, 0)]
    public void ReferenceValueAwardsWholePointsAtTheBountyRate(long cents, int units, int multiplier, long expected) =>
        Assert.Equal(expected, ActivitySkillPointRewards.FromValue(cents / 100m, units, multiplier));

    [Fact]
    public void EveryImplementedIceAndGasDepositHasAPositiveReferenceValue()
    {
        var resources = MiningSiteCatalog.Sites.SelectMany(s => s.Deposits).Select(d => d.TypeId).Distinct().ToArray();
        Assert.NotEmpty(resources);
        foreach (int resource in resources)
        {
            Assert.True(Expected(resource, 400) > 0, $"Missing reward value for resource {resource}.");
        }
    }

    [Fact]
    public void InvalidRewardInputsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ActivitySkillPointRewards.FromValue(-1, 1));
        Assert.Throws<InvalidDataException>(() => ActivitySkillPointRewards.FromValue(1, -1));
        Assert.Throws<InvalidDataException>(() => ActivitySkillPointRewards.FromValue(1, 1, -1));
        Assert.Throws<InvalidDataException>(() => ActivitySkillPointRewards.FromValue(1, 1, 101));
        Assert.Throws<OverflowException>(() => ActivitySkillPointRewards.FromValue(decimal.MaxValue, 2));
    }

    [Theory]
    [InlineData(483, 601, 1230, 1, 1)]
    [InlineData(483, 601, 1230, 2, 3)]
    [InlineData(483, 601, 1230, 2, 0)]
    [InlineData(482, 601, 1230, 1, 1)]
    [InlineData(17482, 17478, 1230, 1, 1)]
    [InlineData(17912, 17478, 1230, 1, 1)]
    [InlineData(18068, 601, 1230, 1, 1)]
    [InlineData(24305, 17478, 1230, 1, 1)]
    [InlineData(16278, 17478, 16265, 1, 1)]
    [InlineData(16278, 17478, 16265, 3, 2)]
    [InlineData(16278, 17478, 16265, 3, 0)]
    [InlineData(37450, 91174, 16265, 1, 1)]
    [InlineData(25266, 32880, 28694, 1, 1)]
    [InlineData(25266, 32880, 28694, 4, 2)]
    [InlineData(25266, 32880, 28694, 4, 0)]
    [InlineData(25812, 32880, 28694, 1, 1)]
    [InlineData(60313, 17478, 28694, 2, 3)]
    public void HarvestCreditsTheScaledDeliveredValueAndPersists(int tool, int hull, int resource, int yield, int spMultiplier)
    {
        using Fixture f = Create(tool, hull, resource, out long module);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        double originalVolume = mining.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.MiningAmount];
        WorldTuningTests.Activate(
            f.Db,
            settings =>
                settings with
                {
                    Resources = settings.Resources with
                    {
                        OreYieldMultiplier = resource == 1230 ? yield : 9,
                        IceYieldMultiplier = resource == 16265 ? yield : 9,
                        GasYieldMultiplier = resource == 28694 ? yield : 9,
                    },
                    Rewards = settings.Rewards with { BountyMultiplier = 0 },
                    Progression = settings.Progression with { KillSkillPointMultiplier = 0, MiningSkillPointMultiplier = spMultiplier },
                }
        );
        var points = f.Db.Services.GetRequiredService<IPilotFreeSkillPoints>();
        Assert.Equal(new(InitialPoints, InitialRevision), points.Observe(f.PilotId));
        var cycle = mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        Assert.Equal(originalVolume * yield, cycle.Laser.Volume, 8);
        Assert.Equal(InitialPoints, points.Balance(f.PilotId));
        var result = mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls));
        int delivered = result.Extracted + result.CriticalBonus;
        Assert.True(delivered > 0);
        if (cycle.Laser.Family == MiningFamily.Ice)
        {
            Assert.Equal(yield, delivered);
        }
        long credit = Expected(resource, delivered, spMultiplier);
        Assert.Equal(spMultiplier > 0, credit > 0);
        Assert.Equal(new(InitialPoints + credit, InitialRevision), points.Observe(f.PilotId));
        Assert.Equal(delivered, result.Cargo!.Item.Quantity);
        Assert.Throws<InvalidDataException>(() => mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls)));
        Assert.Equal(InitialPoints + credit, points.Balance(f.PilotId));
        WorldInitialization.Open(f.Db.Path);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(InitialPoints + credit, reopened.Set<SavedFreeSkillPoints>().Find(f.PilotId)!.Points);
        Assert.Equal(InitialRevision, reopened.Set<SavedFreeSkillPoints>().Find(f.PilotId)!.Revision);
        Assert.Equal(100000 - result.Extracted - result.Residue, reopened.Set<WorldAsteroid>().Find(Rock)!.Quantity);
    }

    [Theory]
    [InlineData(483, 601, 1230)]
    [InlineData(16278, 17478, 16265)]
    [InlineData(25266, 32880, 28694)]
    public void InterruptedCyclesCreditOnlyTheDeliveredUnits(int tool, int hull, int resource)
    {
        using Fixture f = Create(tool, hull, resource, out long module);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        var result = mining.Collect(f.PilotId, module, Rock, new(.5, false, Rolls));
        Assert.Equal(0, result.CriticalBonus);
        Assert.Equal(resource != 16265, result.Extracted > 0);
        Assert.Equal(
            InitialPoints + Expected(resource, result.Extracted),
            f.Db.Services.GetRequiredService<IPilotFreeSkillPoints>().Balance(f.PilotId)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CriticalCargoIsCreditedAndResidueIsExcluded(bool residueOnly)
    {
        using Fixture f = Create(482, 601, 1230, out long module);
        f.Train((int)MiningSkill.Precision, 5);
        f.Train((int)MiningSkill.Exploitation, 5);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldAsteroid>().Find(Rock)!.Quantity = residueOnly ? 1 : 100000;
            world.SaveChanges();
        }
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        var result = mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls with { Critical = 0, Residue = 0 }));
        Assert.True(result.Residue > 0);
        Assert.Equal(!residueOnly, result.CriticalBonus > 0);
        long credit = Expected(1230, result.Extracted + result.CriticalBonus);
        Assert.Equal(InitialPoints + credit, f.Db.Services.GetRequiredService<IPilotFreeSkillPoints>().Balance(f.PilotId));
    }

    [Theory]
    [InlineData(483, 601, 1230)]
    [InlineData(16278, 17478, 16265)]
    [InlineData(25266, 32880, 28694)]
    public void FullHoldsCannotEarnPointsAndFailedCreditsRollBackCollection(int tool, int hull, int resource)
    {
        using Fixture f = Create(tool, hull, resource, out long module);
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<SavedFreeSkillPoints>().Find(f.PilotId)!.Points = long.MaxValue;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls)));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(100000, saved.Set<WorldAsteroid>().Find(Rock)!.Quantity);
        Assert.Equal(long.MaxValue, saved.Set<SavedFreeSkillPoints>().Find(f.PilotId)!.Points);
        Assert.Equal(InitialRevision, saved.Set<SavedFreeSkillPoints>().Find(f.PilotId)!.Revision);
        Assert.NotNull(saved.Set<ModuleCooldown>().Find(module));
        Assert.DoesNotContain(saved.Set<OwnedItem>(), i => i.OwnerId == f.PilotId && i.TypeId == resource);
        saved.Set<SavedFreeSkillPoints>().Find(f.PilotId)!.Points = InitialPoints;
        saved.Add(
            new OwnedItem
            {
                Id = InventoryDefinitions.FirstOwnedItemId + 500000,
                OwnerId = f.PilotId,
                TypeId = resource,
                Quantity = int.MaxValue,
                LocationId = f.ShipId,
                FlagId = (int)(hull == 601 ? InventoryFlag.Cargo : InventoryFlag.GeneralMiningHold),
            }
        );
        saved.SaveChanges();
        var full = mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls));
        Assert.Equal(0, full.Extracted + full.CriticalBonus);
        Assert.Null(full.Cargo);
        Assert.Equal(InitialPoints, f.Db.Services.GetRequiredService<IPilotFreeSkillPoints>().Balance(f.PilotId));
    }

    [Theory]
    [InlineData(483, 601, 1230, false)]
    [InlineData(16278, 17478, 16265, false)]
    [InlineData(25266, 32880, 28694, false)]
    [InlineData(483, 601, 1230, true)]
    [InlineData(16278, 17478, 16265, true)]
    [InlineData(25266, 32880, 28694, true)]
    public void MultipliersCannotAwardPointsForYieldBeyondTheHoldOrDeposit(int tool, int hull, int resource, bool depleted)
    {
        using Fixture f = Create(tool, hull, resource, out long module);
        WorldTuningTests.Activate(
            f.Db,
            settings => settings with { Resources = new(OreYieldMultiplier: 10, IceYieldMultiplier: 10, GasYieldMultiplier: 10) }
        );
        if (depleted)
        {
            using WorldDbContext world = new(f.Db.Path);
            world.Set<WorldAsteroid>().Find(Rock)!.Quantity = 1;
            world.SaveChanges();
        }
        else
        {
            using CatalogDbContext catalog = new(f.CatalogPath);
            using WorldDbContext world = new(f.Db.Path);
            int hold = (int)(hull == 601 ? InventoryFlag.Cargo : InventoryFlag.GeneralMiningHold);
            double capacity = Eden.World.Inventory.PilotInventoryStore.HoldCapacity(world, catalog, f.PilotId, f.ShipId, hold);
            double volume = Eden.World.Inventory.PilotInventoryStore.Volume(catalog, resource);
            world.Add(
                new OwnedItem
                {
                    Id = InventoryDefinitions.FirstOwnedItemId + 500000,
                    OwnerId = f.PilotId,
                    TypeId = resource,
                    Quantity = (int)Math.Floor(capacity / volume) - 1,
                    LocationId = f.ShipId,
                    FlagId = hold,
                }
            );
            world.SaveChanges();
        }
        var mining = f.Db.Services.GetRequiredService<IPilotMining>();
        mining.Begin(f.PilotId, module, Rock, f.Clock.Now);
        var result = mining.Collect(f.PilotId, module, Rock, new(1, true, Rolls));
        Assert.Equal(1, result.Extracted);
        Assert.Equal(0, result.CriticalBonus);
        Assert.Equal(depleted, result.Depleted);
        Assert.Equal(InitialPoints + Expected(resource, 1), f.Db.Services.GetRequiredService<IPilotFreeSkillPoints>().Balance(f.PilotId));
    }

    private static Fixture Create(int tool, int hull, int resource, out long module)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadMining(f.CatalogPath);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.ShipId)!.TypeId = hull;
            world.Add(
                new SavedFreeSkillPoints
                {
                    PilotId = f.PilotId,
                    Points = InitialPoints,
                    Revision = InitialRevision,
                }
            );
            world.Add(
                new WorldAsteroid
                {
                    Id = Rock,
                    SystemId = f.Db.Store.FindPilot(1000001)!.SystemId,
                    TypeId = resource,
                    Quantity = 100000,
                    X = 100,
                    Radius = 10,
                }
            );
            world.SaveChanges();
        }
        module = f.Fit(tool);
        f.Db.Store.SetPosition(f.ShipId, SpacePosition.Zero);
        return f;
    }

    internal static long Expected(int resource, int quantity, decimal multiplier = 1)
    {
        var quote = MarketSnapshotCatalog.Quotes[resource];
        decimal price = MarketValuationRules.EstimateIsk(quote.BuyCents, quote.SellCents)!.Value;
        return (long)decimal.Floor(price * quantity * multiplier / 400m);
    }
}
