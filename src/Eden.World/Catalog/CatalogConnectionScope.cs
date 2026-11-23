using System.Diagnostics;
using Eden.Data.Catalog;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Microsoft.Data.Sqlite;

namespace Eden.World.Catalog;

internal sealed class CatalogConnectionInputs(bool scoped = false) : IDisposable
{
    private SqliteConnection? _connection;
    private SqliteCommand? _record;
    private readonly Lock _recordGate = new();
    internal bool IsScoped { get; } = scoped;

    internal SqliteConnection Connection(string path)
    {
        GameplaySqlGuard.Check("catalog connection");
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

    internal SourceRecord? ReadRecord(string path, string dataset, string key)
    {
        GameplaySqlGuard.Check("catalog record");
        lock (_recordGate)
        {
            if (_record is null)
            {
                _record = Connection(path).CreateCommand();
                _record.CommandText = "SELECT Data FROM SourceRecord WHERE Dataset = $dataset AND Key = $key";
                _record.Parameters.Add("$dataset", SqliteType.Text);
                _record.Parameters.Add("$key", SqliteType.Text);
            }
            _record.Parameters[0].Value = dataset;
            _record.Parameters[1].Value = key;
            SessionPerformance.DatabaseCommand(_record.CommandText);
            long start = Stopwatch.GetTimestamp();
            var data = (string?)_record.ExecuteScalar();
            SessionPerformance.DatabaseElapsed(Stopwatch.GetElapsedTime(start), command: _record.CommandText);
            return data is null
                ? null
                : new SourceRecord
                {
                    Dataset = dataset,
                    Key = key,
                    Data = data,
                };
        }
    }

    public void Dispose()
    {
        _record?.Dispose();
        _connection?.Dispose();
    }
}

internal sealed class CatalogConnectionScope : IPilotOperationScope
{
    private static readonly AsyncLocal<Operation?> s_current = new();

    public IDisposable Begin() => new Operation();

    internal static CatalogConnectionInputs Inputs(string path)
    {
        if (s_current.Value is not { } operation)
        {
            return new();
        }
        if (operation.Catalogs.TryGetValue(path, out var current))
        {
            return current;
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
        internal Dictionary<string, CatalogConnectionInputs> Catalogs { get; }

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
