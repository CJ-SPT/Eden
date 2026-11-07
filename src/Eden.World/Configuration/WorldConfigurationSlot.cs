using Eden.Gameplay.Configuration;

namespace Eden.World.Configuration;

// A host retains its slot through WorldDatabase. Weak registrations do not retain closed test worlds.
internal sealed class WorldConfigurationSlot
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<string, WeakReference<WorldConfigurationSlot>> s_worlds = new(StringComparer.OrdinalIgnoreCase);
    private ConfigurationRevision? _active;
    internal static WorldSettings Defaults { get; } = new();
    internal ConfigurationRevision? Active => Volatile.Read(ref _active);
    internal WorldSettings Settings => Active?.Draft.Settings ?? Defaults;

    internal void Install(ConfigurationRevision revision) => Volatile.Write(ref _active, revision);

    internal static WorldConfigurationSlot For(string path)
    {
        string canonical = Path.GetFullPath(path);
        lock (s_gate)
        {
            if (s_worlds.TryGetValue(canonical, out var reference) && reference.TryGetTarget(out var existing))
            {
                return existing;
            }
            var created = new WorldConfigurationSlot();
            s_worlds[canonical] = new(created);
            foreach (string expired in s_worlds.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray())
            {
                s_worlds.Remove(expired);
            }
            return created;
        }
    }
}
