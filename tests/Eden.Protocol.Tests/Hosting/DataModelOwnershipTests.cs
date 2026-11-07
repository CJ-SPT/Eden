using Eden.Data.World.Characters;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Hosting;

public sealed class DataModelOwnershipTests
{
    [Fact]
    public void RelationalEntitiesBelongToDataWhileContextsRemainInWorld()
    {
        string path = Path.Combine(Path.GetTempPath(), "eden-model-ownership", "world.sqlite");
        using WorldDbContext world = new(path);
        using CatalogDbContext catalog = new(new WorldDatabase(path).CatalogPath);
        var data = typeof(Pilot).Assembly;
        Assert.Equal("Eden.Data", data.GetName().Name);
        foreach (DbContext context in new DbContext[] { world, catalog })
        {
            Assert.Equal("Eden.World", context.GetType().Assembly.GetName().Name);
            var entities = context.Model.GetEntityTypes().ToArray();
            Assert.NotEmpty(entities);
            Assert.All(entities, entity => Assert.Same(data, entity.ClrType.Assembly));
            Assert.All(
                entities,
                entity => Assert.StartsWith(context is WorldDbContext ? "Eden.Data.World." : "Eden.Data.Catalog", entity.ClrType.Namespace)
            );
        }
    }

    [Fact]
    public void DataModelsDoNotReferenceAdaptersOrHosting()
    {
        var references = typeof(Pilot)
            .Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("Eden.", StringComparison.Ordinal))
            .Order()
            .ToArray();
        Assert.Equal(["Eden.Gameplay"], references);
    }
}
