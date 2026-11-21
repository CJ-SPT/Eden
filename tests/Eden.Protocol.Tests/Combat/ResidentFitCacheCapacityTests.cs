using System.Collections.Frozen;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Combat;

public sealed class ResidentFitCacheCapacityTests(ITestOutputHelper output)
{
    private const long Pilot = 90000001;
    private const long FirstBot = 95000000;

    [Fact]
    public void FreshRosterKeepsShipAndMiningFitsWarmAcrossACommittedFitChange()
    {
        using PilotCombatTests.Fixture f = new(liveWorld: true);
        WorldDatabase database = new(f.Db.Path);
        using ResidentFitCache cache = new(database);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var live = f.Db.Services.GetRequiredService<LiveWorld>();
        const int bots = 10000;
        const int miners = 5000;
        Dictionary<DogmaAttributeId, double> ReadFit() =>
            live.Economy.Read(world => PilotFittingStore.ShipAttributes(world, catalog, Pilot), new(pilots: [Pilot]));
        var warmed = ReadFit();
        var hullAttributes = Attributes(96);
        var moduleAttributes = Attributes(20);
        Dictionary<FitCacheKey, FitCacheEntry> roster = new(bots + miners);
        for (long bot = FirstBot; bot < FirstBot + bots; bot++)
        {
            string revision = $"resident:0:0:1:{bot}";
            roster.Add(Key(bot), new(revision, hullAttributes.ToFrozenDictionary()));
            if (bot < FirstBot + miners)
            {
                roster.Add(new(bot, bot, 1), new(revision, moduleAttributes.ToFrozenDictionary()));
            }
        }
        cache.Publish(roster);
        int hits = 0;
        foreach (var (key, fit) in roster)
        {
            if (cache.Read(key, fit.Revision) is { } attributes)
            {
                hits++;
                Assert.Equal(fit.Values, attributes.AsEnumerable());
            }
        }
        using (var warm = SessionPerformance.Begin("active roster fit cache", output.WriteLine, force: true)!)
        {
            Assert.Equal(warmed.OrderBy(pair => pair.Key), ReadFit().OrderBy(pair => pair.Key));
            output.WriteLine($"{hits}/{roster.Count} warm attribute maps, {warm.FittingCalculations} resident hull recalculations.");
            Assert.Equal(roster.Count, hits);
            Assert.Equal(0, warm.FittingCalculations);
            Assert.Equal(0, warm.Commands);
            Assert.Equal(0, warm.Connections);
        }
        using (var world = live.Economy.Begin(new(pilots: [Pilot])))
        {
            world.Set<OwnedItem>().Find(f.Module)!.Online = false;
            world.Commit();
        }
        using var changed = SessionPerformance.Begin("active roster after fit change", output.WriteLine, force: true)!;
        _ = ReadFit();
        Assert.Equal(1, changed.FittingCalculations);
        _ = ReadFit();
        Assert.Equal(1, changed.FittingCalculations);
        Assert.Equal(0, changed.Commands);
        Assert.Equal(0, changed.Connections);
    }

    [Fact]
    public void EntryLimitEvictsTheLeastRecentlyUsedUnpinnedFitAndCatalogChangesClearIt()
    {
        const int entries = 24576;
        WorldDatabase database = new(Path.Combine(TestDirectory.Root, Guid.NewGuid().ToString("N"), "world.sqlite"));
        using ResidentFitCache cache = new(database);
        FitCacheEntry fit = new("first", Attributes(3).ToFrozenDictionary());
        cache.Publish(Enumerable.Range(0, entries).ToDictionary(index => Key(FirstBot + index), _ => fit));
        var observed = Key(FirstBot);
        var recent = Key(FirstBot + 1);
        var idle = Key(FirstBot + 2);
        cache.Pin([observed.PilotId]);
        Assert.NotNull(cache.Read(recent, fit.Revision));
        cache.Publish(new Dictionary<FitCacheKey, FitCacheEntry> { [Key(FirstBot + entries)] = fit });
        Assert.Null(cache.Read(idle, fit.Revision));
        Assert.NotNull(cache.Read(recent, fit.Revision));
        cache.ReleasePilot(observed.PilotId);
        Assert.NotNull(cache.Read(observed, fit.Revision));
        cache.ReleasePilot(recent.PilotId);
        Assert.Null(cache.Read(recent, fit.Revision));

        FitCacheEntry changed = new("second", Attributes(4).ToFrozenDictionary());
        cache.Publish(new Dictionary<FitCacheKey, FitCacheEntry> { [observed] = changed });
        Assert.Null(cache.Read(observed, fit.Revision));
        Assert.Equal(changed.Values, cache.Read(observed, changed.Revision)!.AsEnumerable());
        ResidentFitCache.CatalogChanged(database.OriginalCatalogPath);
        Assert.Null(cache.Read(observed, changed.Revision));
    }

    [Fact]
    public void AttributeSizeLimitBoundsDenseFitsEvenWhenTheEntryLimitIsNotReached()
    {
        const int entries = 4200;
        WorldDatabase database = new(Path.Combine(TestDirectory.Root, Guid.NewGuid().ToString("N"), "world.sqlite"));
        using ResidentFitCache cache = new(database);
        FitCacheEntry fit = new("first", Attributes(512).ToFrozenDictionary());
        cache.Publish(Enumerable.Range(0, entries).ToDictionary(index => Key(FirstBot + index), _ => fit));
        Assert.Null(cache.Read(Key(FirstBot), fit.Revision));
        Assert.NotNull(cache.Read(Key(FirstBot + entries - 1), fit.Revision));
    }

    private static FitCacheKey Key(long pilot) => new(pilot, pilot, 0);

    private static Dictionary<DogmaAttributeId, double> Attributes(int count) =>
        Enumerable.Range(1, count).ToDictionary(index => (DogmaAttributeId)index, index => (double)index);
}
