using Microsoft.Data.Sqlite;

namespace Eden.World.Storage;

// Traffic snapshots own a separate file. They never use the saved world's
// connection or its resident mutation/checkpoint transport.
internal static class TrafficConnections
{
    internal static SqliteConnection Snapshot(string path, bool writable = false) =>
        new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
}

internal sealed class TrafficValidationImage : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=True");

    internal TrafficValidationImage()
    {
        _connection.Open();
    }

    internal WorldDbContext Open() => new(_connection);

    public void Dispose() => _connection.Dispose();
}
