using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    private sealed class ReplayFileLinkFactAttribute : FactAttribute
    {
        public ReplayFileLinkFactAttribute()
        {
            if (OperatingSystem.IsWindows())
            {
                Skip = "Linked replays need file symlink privileges on Windows.";
            }
        }
    }

    [Fact]
    public void CopyFrozenReplayCatalogsAndPrivateState() => VerifyFrozenReplayCopies(linked: false);

    [ReplayFileLinkFact]
    public void LinkFrozenReplayCatalogsAndCopyPrivateState() => VerifyFrozenReplayCopies(linked: true);

    private static void VerifyFrozenReplayCopies(bool linked)
    {
        string source = TestDirectory.Create("eden-frozen-replay-").FullName;
        string? copy = null;
        try
        {
            const string baseCatalog = "catalog.sqlite";
            const string packageCatalog = "world.sqlite.skill-packages/test-package/catalog.sqlite";
            string[] privateFiles =
            [
                "traffic.sqlite",
                "world.sqlite.skills.sqlite",
                "world.sqlite.skills-active",
                "world.sqlite.skill-packages/test-package/manifest.json",
            ];
            RunFrozenReplaySql(Path.Combine(source, "world.sqlite"), "PRAGMA user_version=1", SqliteOpenMode.ReadWriteCreate);
            foreach (string catalog in new[] { baseCatalog, packageCatalog })
            {
                string file = Path.Combine(source, catalog);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                RunFrozenReplaySql(file, "PRAGMA user_version=2", SqliteOpenMode.ReadWriteCreate);
            }
            foreach (string file in privateFiles)
            {
                File.WriteAllText(Path.Combine(source, file), file);
            }
            WriteFrozenReplayManifest(source, [baseCatalog, packageCatalog]);

            FrozenReplayCatalogs? catalogs = linked ? new(source) : null;
            copy = CopySnapshot(source, catalogs);
            CopyThroughputSidecars(source, copy, catalogs);

            string worldCopy = Path.Combine(copy, "world.sqlite");
            Assert.Null(new FileInfo(worldCopy).LinkTarget);
            RunFrozenReplaySql(worldCopy, "PRAGMA user_version=3", SqliteOpenMode.ReadWrite);
            Assert.Equal(3L, RunFrozenReplaySql(worldCopy, "PRAGMA user_version", SqliteOpenMode.ReadOnly));
            Assert.Equal(1L, RunFrozenReplaySql(Path.Combine(source, "world.sqlite"), "PRAGMA user_version", SqliteOpenMode.ReadOnly));
            foreach (string catalog in new[] { baseCatalog, packageCatalog })
            {
                string target = Path.Combine(copy, catalog);
                Assert.Equal(linked ? Path.GetFullPath(Path.Combine(source, catalog)) : null, new FileInfo(target).LinkTarget);
                Assert.Equal(2L, RunFrozenReplaySql(target, "PRAGMA user_version", SqliteOpenMode.ReadOnly));
            }
            foreach (string file in privateFiles)
            {
                string target = Path.Combine(copy, file);
                Assert.Null(new FileInfo(target).LinkTarget);
                File.WriteAllText(target, "edited private copy");
                Assert.Equal(file, File.ReadAllText(Path.Combine(source, file)));
            }
        }
        finally
        {
            if (copy is not null)
            {
                Directory.Delete(copy, recursive: true);
            }
            Directory.Delete(source, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing", "is missing catalog.sqlite")]
    [InlineData("size", "does not match its manifest")]
    [InlineData("hash", "does not match its manifest")]
    [InlineData("header", "must use the SQLite rollback journal format")]
    [InlineData("-journal", "has a SQLite journal sidecar")]
    [InlineData("-wal", "has a SQLite journal sidecar")]
    [InlineData("-shm", "has a SQLite journal sidecar")]
    public void RejectUnfrozenReplayCatalogs(string corruption, string expected)
    {
        string source = TestDirectory.Create("eden-frozen-replay-").FullName;
        try
        {
            const string file = "catalog.sqlite";
            string catalog = Path.Combine(source, file);
            RunFrozenReplaySql(catalog, "PRAGMA user_version=2", SqliteOpenMode.ReadWriteCreate);
            if (corruption == "header")
            {
                Assert.Equal("wal", RunFrozenReplaySql(catalog, "PRAGMA journal_mode=WAL", SqliteOpenMode.ReadWrite));
                Assert.False(File.Exists(catalog + "-wal"));
            }
            if (corruption.StartsWith('-'))
            {
                File.WriteAllBytes(catalog + corruption, []);
            }
            WriteFrozenReplayManifest(source, corruption == "missing" ? [] : [file], corruption);

            string target = Path.Combine(source, "linked.sqlite");
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => new FrozenReplayCatalogs(source).Link(catalog, target));
            Assert.Contains(expected, error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    private static object? RunFrozenReplaySql(string path, string sql, SqliteOpenMode mode)
    {
        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Pooling = false,
            }.ToString()
        );
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void WriteFrozenReplayManifest(string source, string[] catalogs, string corruption = "")
    {
        var files = catalogs.ToDictionary(
            file => file,
            file =>
            {
                byte[] contents = File.ReadAllBytes(Path.Combine(source, file));
                return new
                {
                    bytes = contents.Length + (corruption == "size" ? 1 : 0),
                    sha256 = corruption == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(contents)),
                };
            }
        );
        File.WriteAllText(Path.Combine(source, "manifest.json"), JsonSerializer.Serialize(new { files }));
    }
}
