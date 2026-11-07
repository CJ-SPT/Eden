using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol.Skills;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PassiveFittingTests
{
    internal sealed class Fixture : IDisposable
    {
        internal TestWorld Db { get; }
        internal TestClock Clock { get; } = new();
        internal PilotFittingStore Fitting { get; }
        internal PilotInventoryStore Inventory { get; }
        internal string CatalogPath => new WorldDatabase(Db.Path).CatalogPath;
        internal long PilotId { get; }
        internal long ShipId
        {
            get
            {
                using WorldDbContext world = new(Db.Path);
                return world.Pilots.Where(p => p.Id == PilotId).Select(p => p.ActiveShipId).Single();
            }
        }
        internal long StationId
        {
            get
            {
                using WorldDbContext world = new(Db.Path);
                return world.Pilots.Where(p => p.Id == PilotId).Select(p => p.ActiveShip.StationId).Single();
            }
        }

        internal Fixture()
        {
            Db = new(clock: Clock);
            PassiveCatalogFixture.Load(CatalogPath);
            using WorldDbContext world = new(Db.Path);
            PilotId = world.Pilots.Where(p => p.AccountId == 1000001).Select(p => p.Id).Single();
            Fitting = new(new(Db.Path), Clock);
            Inventory = new(new(Db.Path), Clock);
        }

        internal void Train(int skill, int level)
        {
            using CatalogDbContext catalog = new(CatalogPath);
            double rank = catalog.Set<TypeAttribute>().Find(skill, (int)DogmaAttributeId.SkillTimeConstant)?.Value ?? 1;
            using WorldDbContext world = new(Db.Path);
            var saved =
                world.Set<PilotSkill>().Find(PilotId, skill)
                ?? world.Set<PilotSkill>().Add(new() { PilotId = PilotId, TypeId = skill }).Entity;
            saved.Points = TrainedSkill.PointsForLevel(rank, level);
            world.SaveChanges();
        }

        internal long Grant(int typeId, int quantity = 1, bool cargo = false)
        {
            using WorldDbContext world = new(Db.Path);
            long id = InventoryPersistence.NextId(world);
            world
                .Set<OwnedItem>()
                .Add(
                    new()
                    {
                        Id = id,
                        OwnerId = PilotId,
                        TypeId = typeId,
                        Quantity = quantity,
                        LocationId = cargo ? ShipId : StationId,
                        FlagId = (int)(cargo ? InventoryFlag.Cargo : InventoryFlag.Hangar),
                    }
                );
            world.SaveChanges();
            return id;
        }

        internal long Fit(int typeId, bool online = true)
        {
            using (CatalogDbContext catalog = new(CatalogPath))
            {
                foreach (var (skill, _) in InventoryDefinitions.RequiredSkills)
                {
                    int required = (int)(catalog.Set<TypeAttribute>().Find(typeId, (int)skill)?.Value ?? 0);
                    if (required != 0)
                    {
                        Train(required, 5);
                    }
                }
            }
            long id = Grant(typeId);
            Fitting.Fit(PilotId, [id], StationId, (int)InventoryFlag.AutoFit);
            if (online)
            {
                Fitting.SetOnline(PilotId, ShipId, id, true);
            }
            return id;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void HullProfilesAreCompleteAndRejectActivatedVariantsSharingTheirGroup()
    {
        using Fixture f = new();
        using CatalogDbContext catalog = new(f.CatalogPath);
        var types = catalog.Set<ItemType>().Where(t => PassiveModules.Groups.Contains(t.GroupId)).ToArray();
        var supported = types.Where(t => PassiveCatalog.Supported(catalog, t.Id, t.GroupId)).ToArray();
        Assert.Equal(130, supported.Length);
        Assert.Equal(31, supported.Select(t => t.GroupId).Distinct().Count());
        Assert.Null(FittingCatalog.Definition(catalog, 47254)); // Assault Damage Control I.
        Assert.Null(FittingCatalog.Definition(catalog, 40714)); // Emergency hull energizer.
        Assert.NotNull(FittingCatalog.Definition(catalog, 434)); // Ordinary Tech I microwarpdrive.
        Assert.NotNull(FittingCatalog.Definition(catalog, 2046)); // Ordinary damage control.
        Assert.NotNull(FittingCatalog.Definition(catalog, 1445)); // Both capacitor and booster modifiers are implemented.
    }

    [Fact]
    public void ShieldCapacityAddsBeforeHullSkillsAndKeepsItsChargedPercentage()
    {
        using Fixture f = new();
        f.Train(3419, 3);
        long extender = f.Fit(377, online: false);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world
                .Set<ShipHealthState>()
                .Add(
                    new()
                    {
                        ShipId = f.ShipId,
                        ShieldCharge = 115,
                        ArmorDamage = 20,
                        StructureDamage = 30,
                        UpdatedAt = f.Clock.Now,
                    }
                );
            world.SaveChanges();
        }
        f.Fitting.SetOnline(f.PilotId, f.ShipId, extender, true);
        Assert.Equal(690, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.ShieldCapacity], 8);
        Assert.Equal(1.5, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.PowerLoad], 8);
        Assert.Equal(54, f.Fitting.SignatureRadius(f.ShipId, 601), 8);
        Assert.Equal(52, f.Fitting.SignatureRadius(long.MaxValue, 601), 8);
        using WorldDbContext check = new(f.Db.Path);
        var health = check.Set<ShipHealthState>().Find(f.ShipId)!;
        Assert.Equal(345, health.ShieldCharge, 8);
        Assert.Equal(20, health.ArmorDamage);
        Assert.Equal(30, health.StructureDamage);
        Assert.Equal(f.Clock.Now, health.UpdatedAt);
    }

    [Fact]
    public void CargoExpanderPenaltiesRemainOfflineAndAnOverfilledRemovalRollsBack()
    {
        using Fixture f = new();
        long expander = f.Fit(1317, online: false);
        var offline = f.Fitting.ShipAttributes(f.PilotId);
        Assert.Equal(125, offline[DogmaAttributeId.CargoCapacity]);
        Assert.Equal(295 * 0.85, offline[DogmaAttributeId.MaxVelocity], 8);
        Assert.Equal(175 * 0.8, offline[DogmaAttributeId.StructureHitPoints], 8);
        f.Fitting.SetOnline(f.PilotId, f.ShipId, expander, true);
        Assert.Equal(146.875, f.Db.Store.FindPilot(1000001)!.CargoCapacity, 8);
        long cargo = f.Grant(3651, quantity: 28, cargo: true);
        Assert.Throws<CargoCapacityException>(() => f.Fitting.SetOnline(f.PilotId, f.ShipId, expander, false));
        Assert.Throws<CargoCapacityException>(() =>
            f.Inventory.Move(f.PilotId, f.StationId, new([expander], f.ShipId, (int)InventoryFlag.Hangar, null))
        );
        Assert.True(f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == expander).Online);
        f.Inventory.Move(f.PilotId, f.StationId, new([cargo], f.ShipId, (int)InventoryFlag.Hangar, 3));
        f.Fitting.SetOnline(f.PilotId, f.ShipId, expander, false);
        Assert.Equal(125, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CargoCapacity]);
        Assert.Equal(295 * 0.85, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxVelocity], 8);
        f.Inventory.Move(f.PilotId, f.StationId, new([expander], f.ShipId, (int)InventoryFlag.Hangar, null));
        Assert.Equal(295, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxVelocity], 8);
        Assert.Equal(25, f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == cargo).Quantity);
    }

    [Fact]
    public void RemovingCpuOutputAutomaticallyOfflinesTheLargestConsumerAndPersists()
    {
        using Fixture f = new();
        long processor = f.Fit(3887);
        long civilian = f.Fit(3651);
        long miner = f.Fit(482);
        Assert.Equal(145, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CpuLoad]);
        var changes = f.Fitting.SetOnline(f.PilotId, f.ShipId, processor, false);
        Assert.Equal(new[] { processor, miner }, changes.Select(c => c.Item.Id));
        Assert.All(changes, c => Assert.False(c.Item.Online));
        Assert.True(f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == civilian).Online);
        Assert.Equal(65, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CpuLoad]);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.False(f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == miner).Online);
        Assert.Equal(3, f.Inventory.List(f.PilotId, f.ShipId).Count);
    }

    [Fact]
    public void CompensationSkillsReachTheNativeBrainAndSourceResistanceAttributes()
    {
        using Fixture f = new();
        f.Train(22806, 5);
        long coating = f.Fit(1197);
        var module = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == coating);
        var attributes = f.Fitting.ModuleAttributes(f.PilotId, module);
        using CatalogDbContext catalog = new(f.CatalogPath);
        const int EmResistanceBonus = 984;
        double basis = catalog.Set<TypeAttribute>().Find(1197, EmResistanceBonus)!.Value;
        Assert.Equal(basis * 1.25, attributes[(DogmaAttributeId)EmResistanceBonus], 8);
        var brain = f.Db.Services.GetRequiredService<PilotBrain>().Read(f.PilotId, f.ShipId);
        Assert.Contains(brain.ShipEffects, e => e.Skills.Contains(22806) && e.Value == 25 && e.Extras.Contains(98));
    }
}
