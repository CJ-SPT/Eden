using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eden.Modding;

public static partial class ModPackages
{
    public const long MaximumBytes = 128L * 1024 * 1024;
    public const int MaximumFiles = 4096;
    private const string ManifestPath = "manifest.json";

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$")]
    private static partial Regex IdentityPattern();

    public static void Identity(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !IdentityPattern().IsMatch(id))
        {
            throw new InvalidDataException("Use a lowercase mod identity such as author.example.");
        }
    }

    public static void RelativePath(string path)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || path.Length > 240
            || path.Contains('\\')
            || path.Contains(':')
            || path.Any(char.IsControl)
            || path.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' '))
        )
        {
            throw new InvalidDataException("Mod files require normalized relative paths.");
        }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static void Validate(ModManifest manifest)
    {
        Identity(manifest.Id);
        if (
            manifest.FormatVersion != ModFormat.Version
            || manifest.ApiVersion != ModFormat.ApiVersion
            || manifest.ClientBuild != ModFormat.ClientBuild
            || !Version.TryParse(manifest.Version, out var version)
            || version.Build < 0
            || string.IsNullOrWhiteSpace(manifest.Name)
            || manifest.Name.Length > 200
            || manifest.CatalogFingerprint.Length != 64
            || !manifest.CatalogFingerprint.All(char.IsAsciiHexDigit)
            || manifest.Dependencies.Count > MaximumFiles
            || manifest.Files.Count > MaximumFiles
        )
        {
            throw new InvalidDataException("The mod manifest has incompatible or invalid metadata.");
        }
        HashSet<string> dependencies = new(StringComparer.Ordinal);
        foreach (var dependency in manifest.Dependencies)
        {
            Identity(dependency.Id);
            if (dependency.Id == manifest.Id || !dependencies.Add(dependency.Id) || !Version.TryParse(dependency.Version, out _))
            {
                throw new InvalidDataException("Dependencies must have distinct identities and exact versions.");
            }
        }
        if (manifest.Server is { } server)
        {
            RelativePath(server.Assembly);
            if (!server.Assembly.StartsWith("server/", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(server.Type))
            {
                throw new InvalidDataException("Server plugins require a server assembly and entry type.");
            }
        }
        if (manifest.ClientEntry is { } entry)
        {
            RelativePath(entry);
            if (!entry.StartsWith("client/", StringComparison.Ordinal) || !entry.EndsWith(".py", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Client entries must name a Python source under client/.");
            }
        }
    }

    public static ModPackage Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes)
        {
            throw new InvalidDataException("The mod archive exceeds the size limit.");
        }
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaximumFiles)
        {
            throw new InvalidDataException("Too many mod files.");
        }
        Dictionary<string, byte[]> contents = new(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            RelativePath(entry.FullName);
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumBytes || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
            {
                throw new InvalidDataException("Mod archive expansion or link is invalid.");
            }
            using var input = entry.Open();
            using MemoryStream output = new();
            input.CopyTo(output);
            if (output.Length != entry.Length || !contents.TryAdd(entry.FullName, output.ToArray()))
            {
                throw new InvalidDataException("Duplicate or invalid mod archive entry.");
            }
        }
        if (!contents.Remove(ManifestPath, out var document))
        {
            throw new InvalidDataException("Missing mod manifest.");
        }
        var manifest =
            JsonSerializer.Deserialize<ModManifest>(document, ModFormat.Json) ?? throw new InvalidDataException("Missing mod manifest.");
        Validate(manifest);
        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            RelativePath(file.Path);
            if (
                !declared.Add(file.Path)
                || !contents.TryGetValue(file.Path, out var data)
                || file.Length != data.LongLength
                || file.Sha256 != Hash(data)
            )
            {
                throw new InvalidDataException("A mod file failed verification.");
            }
        }
        if (
            declared.Count != contents.Count
            || manifest.Server is { } plugin && !declared.Contains(plugin.Assembly)
            || manifest.ClientEntry is { } client && !declared.Contains(client)
        )
        {
            throw new InvalidDataException("Mod files do not match the manifest.");
        }
        return new(manifest, hash, contents);
    }

    public static void Write(string path, ModManifest manifest, IReadOnlyDictionary<string, byte[]> contents)
    {
        Validate(manifest);
        var files = contents
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ModFile(pair.Key, Hash(pair.Value), pair.Value.LongLength))
            .ToArray();
        using var output = new FileStream(path, FileMode.CreateNew);
        using ZipArchive archive = new(output, ZipArchiveMode.Create);
        void Add(string name, byte[] bytes)
        {
            RelativePath(name);
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(bytes);
        }
        Add(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { Files = files }, ModFormat.Json));
        foreach (var pair in contents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Key.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The package builder owns manifest.json.");
            }
            Add(pair.Key, pair.Value);
        }
    }

    public static IReadOnlyList<ModPackage> Resolve(IEnumerable<ModPackage> packages, string fingerprint)
    {
        Dictionary<string, ModPackage> indexed = new(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            Validate(package.Manifest);
            if (!indexed.TryAdd(package.Manifest.Id, package) || package.Manifest.CatalogFingerprint != fingerprint)
            {
                throw new InvalidDataException("Duplicate mod identity or mismatched catalog.");
            }
        }
        HashSet<string> visiting = [];
        HashSet<string> visited = [];
        List<ModPackage> ordered = [];
        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }
            if (!visiting.Add(id))
            {
                throw new InvalidDataException("Mod dependencies contain a cycle.");
            }
            var package = indexed[id];
            foreach (var dependency in package.Manifest.Dependencies.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                if (!indexed.TryGetValue(dependency.Id, out var target) || target.Manifest.Version != dependency.Version)
                {
                    throw new InvalidDataException($"{id} requires {dependency.Id} version {dependency.Version}.");
                }
                Visit(dependency.Id);
            }
            visiting.Remove(id);
            visited.Add(id);
            ordered.Add(package);
        }
        foreach (string id in indexed.Keys.Order(StringComparer.Ordinal))
        {
            Visit(id);
        }
        return ordered;
    }
}
