using System.Collections.Concurrent;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.World.Catalog;

[Injectable(InjectionType.Singleton)]
internal sealed class CatalogReadScope : IPilotOperationScope
{
    private static readonly AsyncLocal<Operation?> s_current = new();

    public IDisposable Begin() => new Operation();

    internal static CatalogProvider Resolve(string path, CatalogCacheOptions? options)
    {
        var catalogs = s_current.Value?.Catalogs;
        // World readers usually supply the canonical path already captured for this operation.
        if (catalogs is not null && catalogs.TryGetValue(path, out var current))
        {
            return current;
        }
        string canonical = Path.GetFullPath(path);
        return catalogs is null
            ? CatalogProvider.OpenUnscoped(canonical, options)
            : catalogs.GetOrAdd(canonical, static (key, settings) => CatalogProvider.OpenUnscoped(key, settings), options);
    }

    private sealed class Operation : IDisposable
    {
        private readonly Operation? _previous = s_current.Value;

        // The host resolves one operation scope. Keep catalog generations and
        // connection reuse in that same lifetime instead of competing registrations.
        private readonly IDisposable _connection = new CatalogConnectionScope().Begin();
        internal ConcurrentDictionary<string, CatalogProvider> Catalogs { get; }

        internal Operation()
        {
            Catalogs = _previous?.Catalogs ?? new(StringComparer.OrdinalIgnoreCase);
            s_current.Value = this;
        }

        public void Dispose()
        {
            _connection.Dispose();
            s_current.Value = _previous;
        }
    }
}
