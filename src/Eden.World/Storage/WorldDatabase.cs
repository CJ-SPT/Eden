using System.Collections.Concurrent;

namespace Eden.World.Storage;

public sealed record WorldDatabase(string Path)
{
    internal object MotionLock { get; } = new();
    internal ConcurrentDictionary<int, int> MotionStamps { get; } = new();
    internal ConcurrentDictionary<long, int> CollisionStamps { get; } = new();
    internal ConcurrentDictionary<int, long> PopulationStamps { get; } = new();
    internal ConcurrentDictionary<int, long> PopulationDeadlines { get; } = new();
    public string CatalogPath =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, "catalog.sqlite");
}
