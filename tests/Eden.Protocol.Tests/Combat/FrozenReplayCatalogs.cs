using System.Security.Cryptography;
using System.Text.Json;

namespace Eden.Protocol.Tests.Combat;

internal sealed class FrozenReplayCatalogs
{
    private readonly string _source;
    private readonly JsonElement _files;

    internal FrozenReplayCatalogs(string source)
    {
        _source = Path.GetFullPath(source);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_source, "manifest.json")));
        _files = manifest.RootElement.GetProperty("files").Clone();
    }

    internal void Link(string source, string destination)
    {
        string sourcePath = Path.GetFullPath(source);
        string relativePath = Path.GetRelativePath(_source, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
        if (!_files.TryGetProperty(relativePath, out JsonElement entry))
        {
            throw new InvalidDataException($"Frozen replay manifest is missing {relativePath}.");
        }

        RequireNoJournal(sourcePath);
        using FileStream catalog = File.OpenRead(sourcePath);
        Span<byte> header = stackalloc byte[20];
        catalog.ReadExactly(header);
        if (!header[..16].SequenceEqual("SQLite format 3\0"u8) || header[18] != 1 || header[19] != 1)
        {
            throw new InvalidDataException($"Frozen replay catalog {relativePath} must use the SQLite rollback journal format.");
        }

        catalog.Position = 0;
        if (
            catalog.Length != entry.GetProperty("bytes").GetInt64()
            || !Convert
                .ToHexString(SHA256.HashData(catalog))
                .Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidDataException($"Frozen replay catalog {relativePath} does not match its manifest.");
        }

        RequireNoJournal(sourcePath);
        // The replay operator mounts the frozen source read-only when CatalogDbContext opens these links.
        File.CreateSymbolicLink(destination, sourcePath);
    }

    private static void RequireNoJournal(string source)
    {
        foreach (string suffix in new[] { "-journal", "-wal", "-shm" })
        {
            string sidecar = source + suffix;
            if (File.Exists(sidecar))
            {
                throw new InvalidDataException($"Frozen replay catalog has a SQLite journal sidecar at {sidecar}.");
            }
        }
    }
}
