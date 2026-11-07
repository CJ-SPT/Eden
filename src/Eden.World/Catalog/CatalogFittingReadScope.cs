using Eden.Gameplay.Dogma;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.World.Catalog;

internal sealed class CatalogFittingInputs
{
    internal Dictionary<(string Dataset, string Key), SourceRecord?> Sources { get; } = [];
    internal Dictionary<int, ItemType?> Types { get; } = [];
    internal Dictionary<int, IReadOnlyDictionary<DogmaAttributeId, double>> Attributes { get; } = [];
}

[Injectable(InjectionType.Singleton)]
internal sealed class CatalogFittingReadScope : IPilotOperationScope
{
    private static readonly AsyncLocal<Operation?> s_current = new();

    public IDisposable Begin() => new Operation();

    internal static CatalogFittingInputs Inputs(string path)
    {
        if (s_current.Value is not { } operation)
        {
            return new();
        }
        string fullPath = Path.GetFullPath(path);
        if (!operation.Catalogs.TryGetValue(fullPath, out var inputs))
        {
            inputs = new();
            operation.Catalogs.Add(fullPath, inputs);
        }
        return inputs;
    }

    private sealed class Operation : IDisposable
    {
        private readonly Operation? _previous = s_current.Value;
        internal Dictionary<string, CatalogFittingInputs> Catalogs { get; }

        internal Operation()
        {
            Catalogs = _previous?.Catalogs ?? new(StringComparer.OrdinalIgnoreCase);
            s_current.Value = this;
        }

        public void Dispose() => s_current.Value = _previous;
    }
}
