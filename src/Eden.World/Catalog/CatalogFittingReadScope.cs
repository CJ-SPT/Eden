using Eden.Data.Catalog;
using Eden.Gameplay.Dogma;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Microsoft.Data.Sqlite;

namespace Eden.World.Catalog;

internal sealed class CatalogFittingInputs(bool scoped = false) : IDisposable
{
    private SqliteConnection? _connection;
    internal bool IsScoped { get; } = scoped;
    internal Dictionary<(string Dataset, string Key), SourceRecord?> Sources { get; } = [];
    internal Dictionary<int, ItemType?> Types { get; } = [];
    internal Dictionary<int, IReadOnlyDictionary<DogmaAttributeId, double>> Attributes { get; } = [];

    internal SqliteConnection Connection(string path)
    {
        if (_connection is null)
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString()
            );
            try
            {
                connection.Open();
                SessionPerformance.ConnectionOpened();
                _connection = connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        return _connection;
    }

    public void Dispose() => _connection?.Dispose();
}

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
            inputs = new(scoped: true);
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

        public void Dispose()
        {
            s_current.Value = _previous;
            if (_previous is null)
            {
                foreach (var inputs in Catalogs.Values)
                {
                    inputs.Dispose();
                }
            }
        }
    }
}
