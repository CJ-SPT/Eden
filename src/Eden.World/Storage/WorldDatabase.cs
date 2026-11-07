using System.Collections.Concurrent;
using Eden.World.Catalog;
using Eden.World.Configuration;

namespace Eden.World.Storage;

public sealed record WorldDatabase(string Path)
{
    internal WorldConfigurationSlot Configuration { get; } = WorldConfigurationSlot.For(Path);
    public Eden.Gameplay.Configuration.WorldSettings Settings => Configuration.Settings;
    public CatalogProvider Catalog => CatalogProvider.Open(CatalogPath);
    internal object MotionLock { get; } = new();
    internal ConcurrentDictionary<int, int> MotionStamps { get; } = new();
    internal ConcurrentDictionary<long, int> CollisionStamps { get; } = new();
    internal ConcurrentDictionary<int, long> PopulationStamps { get; } = new();
    internal ConcurrentDictionary<int, long> PopulationDeadlines { get; } = new();
    public string CatalogPath =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, "catalog.sqlite");
}
