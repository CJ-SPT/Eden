using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Mining;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Mining;

public sealed class MiningCrystalTests
{
    private const int ModulatedMiner = 18068;
    private const int SimpleCrystal = 60276;
    private const int SimpleProcessing = 60377;
    private const int Veldspar = 1230;
    private const long Asteroid = AsteroidDefinitions.FirstAsteroidId;
    private static readonly MiningRolls Rolls = new(.99, .99, .99, .99, .99, 0);

    private sealed class MiningFixture : IDisposable
    {
        internal Fixture F { get; } = new();
        internal IPilotMining Mining => F.Db.Services.GetRequiredService<IPilotMining>();
        internal PilotAmmunitionStore Ammo => new(new(F.Db.Path));
        internal long Module { get; }

        internal MiningFixture()
        {
            PassiveCatalogFixture.LoadMining(F.CatalogPath);
            Module = F.Fit(ModulatedMiner);
            F.Train(SimpleProcessing, 5);
            using WorldDbContext world = new(F.Db.Path);
            world
                .Set<WorldAsteroid>()
                .Add(
                    new()
                    {
                        Id = Asteroid,
                        BeltId = 40009087,
                        SystemId = F.Db.Store.FindPilot(1000001)!.SystemId,
                        TypeId = Veldspar,
                        Quantity = 10000,
                        X = 1500,
                        Radius = 100,
                    }
                );
            world.SaveChanges();
        }

        internal void Load(int type = SimpleCrystal)
        {
            long crystal = F.Grant(type, 2);
            Ammo.Load(F.PilotId, F.ShipId, [Module], [crystal], F.StationId, F.Clock.Now);
            F.Clock.Milliseconds++;
        }

        internal void Undock() => F.Db.Store.SetPosition(F.ShipId, SpacePosition.Zero);

        public void Dispose() => F.Dispose();
    }

    [Fact]
    public void MatchingCrystalChangesYieldAndPersistsWearAcrossShortCycleAndReload()
    {
        using MiningFixture f = new();
        f.Load();
        f.Undock();
        var start = f.Mining.Begin(f.F.PilotId, f.Module, Asteroid, f.F.Clock.Now);
        Assert.Equal(56.25, start.Laser.Volume, 8);
        Assert.Equal(45000, start.Laser.DurationMilliseconds);
        var result = f.Mining.Collect(f.F.PilotId, f.Module, Asteroid, new(.5, false, Rolls));
        Assert.Equal(281, result.Extracted);
        Assert.Equal(.05, result.Crystal!.Damage);
        Assert.Equal(1, result.Crystal.Quantity);
        WorldInitialization.Open(f.F.Db.Path);
        Assert.Equal(.05, Assert.Single(f.Ammo.Read(f.F.PilotId, f.F.ShipId)).Damage);
        var transfer = f.Ammo.Unload(f.F.PilotId, f.F.ShipId, [f.Module], f.F.ShipId, (int)InventoryFlag.Cargo, null, f.F.Clock.Now);
        Assert.Equal(.05, Assert.Single(transfer.Items).Item.CrystalDamage);
        using WorldDbContext world = new(f.F.Db.Path);
        Assert.Equal(9719, world.Set<WorldAsteroid>().Find(Asteroid)!.Quantity);
    }

    [Theory]
    [InlineData(60276, 1.5, 1, 23, .34, 1)]
    [InlineData(60279, 1.5, .9, 27.6, .54, 1)]
    [InlineData(60280, .25, 1, 34.5, .74, 19)]
    [InlineData(60281, 1.8, 1, 23, .376, 1)]
    public void CrystalVariantsApplyPinnedYieldTimeCapacitorAndResidue(
        int crystal,
        double yield,
        double duration,
        double capacitor,
        double residue,
        double multiplier
    )
    {
        using MiningFixture f = new();
        f.Load(crystal);
        f.Undock();
        var laser = f.Mining.Begin(f.F.PilotId, f.Module, Asteroid, f.F.Clock.Now).Laser;
        Assert.Equal(37.5 * yield, laser.Volume, 8);
        Assert.Equal(45000 * duration, laser.DurationMilliseconds, 8);
        Assert.Equal(capacitor, laser.CapacitorNeed, 8);
        Assert.Equal(residue, laser.ResidueChance, 8);
        Assert.Equal(multiplier, laser.ResidueMultiplier, 8);
    }

    [Fact]
    public void WrongOreRejectsBeforeSpendingOrWearingAndShatteredCrystalIsRemoved()
    {
        using MiningFixture f = new();
        f.Load();
        f.Undock();
        using (WorldDbContext world = new(f.F.Db.Path))
        {
            world.Set<WorldAsteroid>().Find(Asteroid)!.TypeId = 1223;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => f.Mining.Begin(f.F.PilotId, f.Module, Asteroid, f.F.Clock.Now));
        Assert.Equal(0, Assert.Single(f.Ammo.Read(f.F.PilotId, f.F.ShipId)).Damage);
        using (WorldDbContext world = new(f.F.Db.Path))
        {
            world.Set<WorldAsteroid>().Find(Asteroid)!.TypeId = Veldspar;
            world.Set<OwnedItem>().Single(i => i.LocationId == f.Module).CrystalDamage = .975;
            world.SaveChanges();
        }
        f.Mining.Begin(f.F.PilotId, f.Module, Asteroid, f.F.Clock.Now);
        var result = f.Mining.Collect(f.F.PilotId, f.Module, Asteroid, new(1, true, Rolls));
        Assert.Equal(1, result.Crystal!.Damage);
        Assert.Equal(0, result.Crystal.Quantity);
        Assert.Empty(f.Ammo.Read(f.F.PilotId, f.F.ShipId));
        Assert.Equal(37.5, f.Mining.Begin(f.F.PilotId, f.Module, Asteroid, f.F.Clock.Now).Laser.Volume, 8);
    }

    [Fact]
    public void CatalogAdmitsStaticModulatedMinersAndEveryPublishedOrdinaryCrystal()
    {
        using MiningFixture f = new();
        using CatalogDbContext catalog = new(f.F.CatalogPath);
        foreach (int type in new[] { 17912, 18068, 24305 })
        {
            Assert.NotNull(FittingCatalog.Definition(catalog, type));
        }
        var crystals = catalog
            .Records(Eden.World.Catalog.CatalogData.Client.Types)
            .Where(p => p.Value.Published == 1 && p.Value.GroupID == MiningCrystalRules.Group)
            .ToArray();
        Assert.Equal(66, crystals.Length);
        Assert.All(crystals, p => Assert.True(MiningCrystalCatalog.Supported(catalog, checked((int)p.Key), MiningCrystalRules.Group)));
        Assert.Null(FittingCatalog.Definition(catalog, 90474));
        var coverage = ShipContentAudit.Read(catalog);
        Assert.Equal(ContentSupportState.Complete, coverage.Entries.Single(e => e.TypeId == 17912).State);
        foreach (int type in new[] { 18068, 24305 })
        {
            Assert.Equal(ContentSupportState.Partial, coverage.Entries.Single(e => e.TypeId == type).State);
        }
    }

    [Fact]
    public void PaidCyclesRejectReplayAndActiveCrystalChangesWithoutChangingSavedItems()
    {
        using MiningFixture f = new();
        f.Load();
        f.Undock();
        f.Mining.Begin(f.F.PilotId, f.Module, Asteroid, f.F.Clock.Now);
        Assert.Throws<InvalidDataException>(() => f.Mining.Begin(f.F.PilotId, f.Module, Asteroid, f.F.Clock.Now));
        Assert.Throws<InvalidDataException>(() =>
            f.Ammo.Unload(f.F.PilotId, f.F.ShipId, [f.Module], f.F.ShipId, (int)InventoryFlag.Cargo, null, f.F.Clock.Now)
        );
        var result = f.Mining.Collect(f.F.PilotId, f.Module, Asteroid, new(.5, false, Rolls));
        Assert.Throws<InvalidDataException>(() => f.Mining.Collect(f.F.PilotId, f.Module, Asteroid, new(.5, false, Rolls)));
        Assert.Equal(result.Crystal!.Damage, Assert.Single(f.Ammo.Read(f.F.PilotId, f.F.ShipId)).Damage);
        using WorldDbContext world = new(f.F.Db.Path);
        Assert.Equal(10000 - result.Extracted - result.Residue, world.Set<WorldAsteroid>().Find(Asteroid)!.Quantity);
    }

    [Fact]
    public void UnsupportedCrystalLoadPreservesTheExistingCrystalAndSourceStack()
    {
        using MiningFixture f = new();
        f.Load();
        long unsupported = f.F.Grant(18054, 2);
        var before = f.F.Inventory.List(f.F.PilotId, f.F.StationId).ToArray();
        Assert.Throws<InvalidDataException>(() =>
            f.Ammo.Load(f.F.PilotId, f.F.ShipId, [f.Module], [unsupported], f.F.StationId, f.F.Clock.Now)
        );
        Assert.Equal(before, f.F.Inventory.List(f.F.PilotId, f.F.StationId));
        Assert.Equal(SimpleCrystal, Assert.Single(f.Ammo.Read(f.F.PilotId, f.F.ShipId)).TypeId);
    }
}
