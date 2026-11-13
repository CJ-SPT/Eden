using System.Diagnostics;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldDatabaseCopyTests(ITestOutputHelper output)
{
    [Fact]
    public void DetachedImagePreservesSchemaIsolationGrowthAndRollback()
    {
        using var source = Open();
        Execute(
            source,
            """
            CREATE TABLE Parent(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);
            CREATE TABLE Child(Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES Parent(Id), Payload BLOB);
            CREATE INDEX ChildParent ON Child(ParentId);
            CREATE TRIGGER KeepParent BEFORE DELETE ON Parent BEGIN SELECT RAISE(ABORT,'keep parent'); END;
            INSERT INTO Parent VALUES(1,'captured');
            INSERT INTO Child VALUES(1,1,zeroblob(65536));
            PRAGMA user_version=7;
            """
        );
        using var destination = Open();
        WorldDatabaseCopy.Copy(source, destination);
        Execute(source, "UPDATE Parent SET Name='advanced'");
        Assert.Equal("captured", Scalar(destination, "SELECT Name FROM Parent"));
        Assert.Equal(7L, Scalar(destination, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(destination, "SELECT COUNT(*) FROM sqlite_schema WHERE name='ChildParent'"));
        Assert.Throws<SqliteException>(() => Execute(destination, "DELETE FROM Parent"));
        Assert.Throws<SqliteException>(() => Execute(destination, "INSERT INTO Child VALUES(2,2,NULL)"));
        Execute(destination, "BEGIN; INSERT INTO Child VALUES(2,1,zeroblob(1048576)); ROLLBACK;");
        Assert.Equal(1L, Scalar(destination, "SELECT COUNT(*) FROM Child"));
        Execute(destination, "INSERT INTO Child VALUES(2,1,zeroblob(1048576))");
        Assert.Equal(1L, Scalar(source, "SELECT COUNT(*) FROM Child"));

        string path = Path.Combine(Path.GetTempPath(), "eden-copy-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            LiveWorld.PersistSnapshot(destination, path);
            using SqliteConnection reopened = new($"Data Source={path};Pooling=False;Foreign Keys=True");
            reopened.Open();
            Assert.Equal("captured", Scalar(reopened, "SELECT Name FROM Parent"));
            Assert.Equal(1048576L, Scalar(reopened, "SELECT length(Payload) FROM Child WHERE Id=2"));
            Assert.Equal("ok", Scalar(reopened, "PRAGMA integrity_check"));
            Assert.Equal(0L, Scalar(reopened, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".previous");
        }
    }

    [Fact]
    public void LargeImageCopyUsesDetachedNativeStorageWithoutManagedImageAllocation()
    {
        const int payloadBytes = 32 * 1024 * 1024;
        using var source = Open();
        Execute(source, $"CREATE TABLE Payload(Value BLOB); INSERT INTO Payload VALUES(zeroblob({payloadBytes}));");
        for (int sample = 0; sample < 4; sample++)
        {
            using var backup = Open();
            long started = Stopwatch.GetTimestamp();
            source.BackupDatabase(backup);
            var baseline = Stopwatch.GetElapsedTime(started);
            using var image = Open();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            using var metrics = SessionPerformance.Begin("database copy", output.WriteLine, force: true)!;
            started = Stopwatch.GetTimestamp();
            WorldDatabaseCopy.Copy(source, image);
            var copy = Stopwatch.GetElapsedTime(started);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < payloadBytes / 100);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
            // NOCOPY exposes the destination's own buffer; it must never be freed by this reader.
            nint buffer = raw.sqlite3_serialize(image.Handle!, "main", out long size, raw.SQLITE_SERIALIZE_NOCOPY);
            Assert.NotEqual(0, buffer);
            Assert.True(size > payloadBytes);
            Assert.Equal((long)payloadBytes, Scalar(image, "SELECT length(Value) FROM Payload"));
            Execute(image, "DELETE FROM Payload");
            Assert.Equal(1L, Scalar(source, "SELECT COUNT(*) FROM Payload"));
            output.WriteLine($"Sample {sample}: backup={baseline.TotalMilliseconds:F2} ms, image={copy.TotalMilliseconds:F2} ms.");
        }
    }

    private static SqliteConnection Open()
    {
        SqliteConnection database = new("Data Source=:memory:;Pooling=False;Foreign Keys=True");
        database.Open();
        return database;
    }

    private static void Execute(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
