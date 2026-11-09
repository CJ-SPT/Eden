using System.Collections.Concurrent;
using Eden.World.Catalog;
using Eden.World.Configuration;

namespace Eden.World.Storage;

public sealed record WorldDatabase(string Path)
{
    private Eden.World.Capsuleers.TrafficSnapshot? _traffic;
    internal Eden.World.Capsuleers.TrafficSnapshot Traffic => _traffic ??= ReadTraffic();

    internal void LoadTraffic() => _traffic = ReadTraffic();

    private Eden.World.Capsuleers.TrafficSnapshot ReadTraffic()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, "traffic.sqlite");
        if (!File.Exists(path))
        {
            return Eden.World.Capsuleers.TrafficSnapshot.Empty;
        }
        try
        {
            using CatalogDbContext catalog = new(CatalogPath);
            return Eden.World.Capsuleers.TrafficSnapshot.Read(path, Eden.World.Capsuleers.TrafficSnapshot.Fingerprint(catalog));
        }
        catch (Exception error)
            when (error is IOException or InvalidDataException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"Traffic snapshot unavailable; using generated travelers: {error.Message}");
            return Eden.World.Capsuleers.TrafficSnapshot.Empty;
        }
    }

    internal WorldConfigurationSlot Configuration { get; } = WorldConfigurationSlot.For(Path);
    public Eden.Gameplay.Configuration.WorldSettings Settings => Configuration.Settings;
    public CatalogProvider Catalog => CatalogProvider.Open(CatalogPath);
    internal object MotionLock { get; } = new();
    internal ConcurrentDictionary<int, int> MotionStamps { get; } = new();
    internal ConcurrentDictionary<long, int> CollisionStamps { get; } = new();
    internal ConcurrentDictionary<int, long> PopulationStamps { get; } = new();
    internal ConcurrentDictionary<int, long> PopulationDeadlines { get; } = new();
    public string OriginalCatalogPath =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, "catalog.sqlite");
    private string? _catalogPath;
    public string CatalogPath => _catalogPath ??= Eden.World.Skills.SkillPackageActivation.CatalogPath(Path, OriginalCatalogPath);
    internal IReadOnlyDictionary<int, Eden.Gameplay.Skills.PublishedSkillDefinition> PublishedSkills => Catalog.PublishedSkills;

    internal void ResetSkillCatalog()
    {
        _catalogPath = null;
    }
}
