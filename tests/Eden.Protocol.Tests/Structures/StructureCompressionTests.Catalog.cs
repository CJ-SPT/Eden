using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed partial class StructureCompressionTests
{
    [Fact]
    public void All212PinnedMappingsRespectTheStructureTypeListWithoutChangingUnitCounts()
    {
        using Fixture f = new();
        var facility = Prepare(f);
        var compression = f.Db.Services.GetRequiredService<IStructureCompression>();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var pairs = catalog.Definitions.Pin(CatalogData.Client.Compressibletypes);
        Assert.Equal(212, pairs.Count);
        var allowed = CatalogTypeLists.Read(catalog, StructureCompressionRules.StructureTypeList);
        int accepted = 0;
        foreach (var pair in pairs.OrderBy(p => p.Key))
        {
            int source = checked((int)pair.Key);
            var type = catalog.RequireItem(source);
            long id = HangarItem(f, facility.Id, source, 37);
            if (allowed.Contains(type.Id, type.GroupId, type.CategoryId))
            {
                var result = compression.Compress(f.PilotId, id);
                Assert.Equal(pair.Value, result.OutputTypeId);
                Assert.Equal(id, result.OutputItemId);
                Assert.Equal(37, result.OutputQuantity);
                Assert.Equal(source, result.Change.Previous!.TypeId);
                Assert.True(result.Change.Item.Volume < result.Change.Previous.Volume);
                accepted++;
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => compression.Compress(f.PilotId, id));
                using WorldDbContext world = new(f.Db.Path);
                var unchanged = world.Set<OwnedItem>().Find(id)!;
                Assert.Equal(source, unchanged.TypeId);
                Assert.Equal(37, unchanged.Quantity);
            }
        }
        Assert.Equal(127, accepted);
    }

    [Fact]
    public void EveryCompressedGasPairDecompressesToItsUniqueRawTypeWithFlooredLoss()
    {
        using Fixture f = new();
        var facility = Prepare(f);
        var compression = f.Db.Services.GetRequiredService<IStructureCompression>();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var gas = catalog
            .Definitions.Pin(CatalogData.Client.Compressibletypes)
            .Where(p => catalog.RequireItem(checked((int)p.Value)).GroupId == (int)HoldCargoGroup.CompressedGas)
            .ToArray();
        Assert.Equal(25, gas.Length);
        foreach (var pair in gas)
        {
            long id = HangarItem(f, facility.Id, checked((int)pair.Value), 101);
            var result = compression.DecompressGas(f.PilotId, id);
            Assert.Equal(pair.Key, result.OutputTypeId);
            Assert.Equal(80, result.OutputQuantity);
            Assert.Equal(id, result.OutputItemId);
            Assert.Equal(101, result.SourceQuantity);
            Assert.Equal(pair.Value, result.Change.Previous!.TypeId);
            Assert.Equal(pair.Key, result.Change.Item.TypeId);
        }
    }

    [Theory]
    [InlineData(35832, 0)]
    [InlineData(35835, 4)]
    [InlineData(35836, 10)]
    public void CatalogServiceRefineryAndAllSkillLevelsControlDecompressionEfficiency(int hull, int hullPercent)
    {
        using Fixture f = new();
        Prepare(f, hull);
        var compression = f.Db.Services.GetRequiredService<IStructureCompression>();
        for (int level = 0; level <= 5; level++)
        {
            using (WorldDbContext world = new(f.Db.Path))
            {
                var skill =
                    world.Set<PilotSkill>().Find(f.PilotId, StructureCompressionRules.GasDecompressionSkill)
                    ?? world.Add(new PilotSkill { PilotId = f.PilotId, TypeId = StructureCompressionRules.GasDecompressionSkill }).Entity;
                skill.Points = TrainedSkill.PointsForLevel(2, level);
                world.SaveChanges();
            }
            Assert.Equal(new(.8m + hullPercent / 100m, level / 100m), compression.Efficiency(f.PilotId));
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(50, 40)]
    [InlineData(101, 80)]
    public void TypedDecompressionFloorsWholeUnitsAndCountsBothEfficiencyTerms(int quantity, int expected)
    {
        var efficiency = StructureCompressionRules.Efficiency(.8m, 0, 1, 0);
        Assert.Equal(expected, StructureCompressionRules.Decompress(quantity, efficiency));
        Assert.Equal(.95m, StructureCompressionRules.Efficiency(.8m, .1m, 1, 5).Total);
        Assert.Equal(42, StructureCompressionRules.Decompress(50, new(.8m, .04m)));
        Assert.Equal(2_040_109_464, StructureCompressionRules.Decompress(int.MaxValue, new(.9m, .05m)));
        Assert.Equal(int.MaxValue, StructureCompressionRules.Decompress(int.MaxValue, new(1, 1)));
        Assert.Throws<InvalidDataException>(() => StructureCompressionRules.Decompress(0, efficiency));
        Assert.Throws<InvalidDataException>(() => StructureCompressionRules.Decompress(-1, efficiency));
        Assert.Throws<InvalidDataException>(() => StructureCompressionRules.Decompress(1, new(-.1m, 0)));
        Assert.Throws<InvalidDataException>(() => StructureCompressionRules.Efficiency(.8m, 0, 1, 6));
        Assert.Throws<InvalidDataException>(() => StructureCompressionRules.Efficiency(1.1m, 0, 1, 0));
        Assert.Throws<InvalidDataException>(() => StructureCompressionRules.Efficiency(.8m, -.1m, 1, 0));
        Assert.Throws<InvalidDataException>(() => StructureCompressionRules.Efficiency(.8m, 0, -1, 0));
    }
}
