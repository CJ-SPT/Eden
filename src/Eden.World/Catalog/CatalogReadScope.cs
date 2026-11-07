using System.Collections.Concurrent;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.World.Catalog;

[Injectable(InjectionType.Singleton)]
internal sealed class CatalogReadScope : IPilotOperationScope
{
    private static readonly AsyncLocal<Operation?> s_current = new();

    public IDisposable Begin() => new Operation();

    internal static CatalogProvider Resolve(string path, Func<CatalogProvider> load)
    {
        if (s_current.Value is not { } operation)
        {
            return load();
        }
        return operation.Catalogs.GetOrAdd(path, _ => load());
    }

    private sealed class Operation : IDisposable
    {
        private readonly Operation? _previous = s_current.Value;
        internal ConcurrentDictionary<string, CatalogProvider> Catalogs { get; }

        internal Operation()
        {
            Catalogs = _previous?.Catalogs ?? new(StringComparer.OrdinalIgnoreCase);
            s_current.Value = this;
        }

        public void Dispose() => s_current.Value = _previous;
    }
}
