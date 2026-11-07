using Eden.Data.Catalog;
using Eden.Gameplay.Content;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CatalogAdoptionTests
{
    [Fact]
    public void TypedStringKeysRetainImporterUnicodeHtmlAndControlEscapes()
    {
        const string key = "Δ<&\u001a";
        const string encoded = "\"Δ<&\\u001a\"";
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        using (CatalogDbContext fixture = new(path, writable: true))
        {
            fixture.Add(
                new SourceRecord
                {
                    Dataset = "client/industry_activities",
                    Key = encoded,
                    Data = "{}",
                }
            );
            fixture.SaveChanges();
        }
        var provider = CatalogProvider.Open(path);
        var record = provider.Get(CatalogData.Client.IndustryActivities, key);
        Assert.Same(record, provider.FindMany(CatalogData.Client.IndustryActivities, [key])[key]);
        Assert.Equal(key, Assert.Single(provider.Page(CatalogData.Client.IndustryActivities).Rows).Key);
        var stored = Assert.Single(new CatalogBrowser(provider).Page("client/industry_activities", key: encoded).Rows);
        Assert.Equal(encoded, stored.Key);
    }

    [Fact]
    public void RecognizingAProfileDoesNotAdmitUnlistedOrUnsupportedContent()
    {
        HullAssemblyProfile supported = new((int)TechOneHullType.Merlin, InventoryCategory.Ship, true, 1);
        Assert.True(TechOneHullRules.CanAssemble(supported));
        Assert.False(TechOneHullRules.CanAssemble(supported with { TypeId = int.MaxValue }));
        Assert.False(TechOneHullRules.CanAssemble(supported with { TechLevel = 2 }));
        Assert.False(TechOneHullRules.CanAssemble(supported with { Published = false }));
        Assert.False(DroneRules.SupportsHull(int.MaxValue));
        Assert.False(DroneRules.SupportsHull(supported.TypeId));
        Assert.Equal(BuildContentPolicy.PinnedBuild, CatalogSchemas.Report.Build);
    }

    [Fact]
    public void BrowsingMetadataCannotMutateSharedSchemaOrNativeFieldCompatibility()
    {
        var descriptor = CatalogSchemas.Get("client/types");
        var fields = descriptor.ValueSchema.Fields!;
        Assert.False(fields["volume"].Compatibility.Editable);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, Eden.Data.Catalog.CatalogShapeDescriptor>)fields).Clear());
        Assert.True(CatalogSchemas.Get("client/types").ValueSchema.Fields!.ContainsKey("volume"));
        Assert.Equal(0, CatalogSchemas.Report.UnresolvedFields);
        Assert.Equal(442, CatalogSchemas.Report.Datasets.Length);
    }
}
