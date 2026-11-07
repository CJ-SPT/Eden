using System.Security.Cryptography;
using System.Text;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Fixtures;

internal sealed record TestWorldSeed(byte[] Catalog, byte[] World)
{
    private static readonly Lazy<TestWorldSeed> Default = new(() =>
    {
        string directory = AppContext.BaseDirectory;
        string[] inputs = [.. Directory.GetFiles(directory, "Eden*.dll"), Path.Combine(directory, "Eden.Protocol.Tests.deps.json")];
        return Load(Path.Combine(TestDirectory.Root, "seeds"), Fingerprint(inputs));
    });

    internal static string Fingerprint(IEnumerable<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void CopyTo(WorldDatabase database)
    {
        TestWorldSeed seed = Default.Value;
        File.WriteAllBytes(database.CatalogPath, seed.Catalog);
        File.WriteAllBytes(database.Path, seed.World);
    }

    internal static TestWorldSeed Load(string root, string fingerprint)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, fingerprint);
        string mutexKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
        using Mutex mutex = new(false, "EdenTestSeed-" + mutexKey);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromMinutes(1));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        if (!acquired)
        {
            throw new TimeoutException("Another test process did not finish preparing the Eden seed.");
        }
        try
        {
            if (Read(path) is { } cached)
            {
                return cached;
            }
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            string staging = Path.Combine(root, fingerprint + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                WorldDatabase database = new(Path.Combine(staging, "source.sqlite"));
                TestCatalog.Create(database.CatalogPath);
                WorldInitialization.Initialize(database);
                using (WorldDbContext world = new(database.Path))
                {
                    world.Database.ExecuteSqlRaw("DELETE FROM WorldTestingGrant; DELETE FROM WorldWallet");
                }
                // Backup includes committed WAL pages and produces standalone snapshots.
                Snapshot(database.CatalogPath, Path.Combine(staging, "catalog.seed"));
                Snapshot(database.Path, Path.Combine(staging, "world.seed"));
                byte[] catalog = File.ReadAllBytes(Path.Combine(staging, "catalog.seed"));
                byte[] worldBytes = File.ReadAllBytes(Path.Combine(staging, "world.seed"));
                File.WriteAllLines(Path.Combine(staging, "checksums"), [Checksum(catalog), Checksum(worldBytes)]);
                foreach (string source in Directory.GetFiles(staging, "*.sqlite*"))
                {
                    File.Delete(source);
                }
                Directory.Move(staging, path);
                return new(catalog, worldBytes);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static TestWorldSeed? Read(string path)
    {
        try
        {
            byte[] catalog = File.ReadAllBytes(Path.Combine(path, "catalog.seed"));
            byte[] world = File.ReadAllBytes(Path.Combine(path, "world.seed"));
            string[] checksums = File.ReadAllLines(Path.Combine(path, "checksums"));
            return checksums.Length == 2 && checksums[0] == Checksum(catalog) && checksums[1] == Checksum(world)
                ? new(catalog, world)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Checksum(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Snapshot(string source, string destination)
    {
        using SqliteConnection input = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        using SqliteConnection output = new(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        input.Open();
        output.Open();
        input.BackupDatabase(output);
    }
}
