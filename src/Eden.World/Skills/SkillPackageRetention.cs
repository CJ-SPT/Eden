using Eden.Data.World.Modding;
using Eden.Data.World.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Modding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

public sealed class SkillPackageRetention : IDisposable
{
    private readonly string _worldPath;
    private readonly FileStream _ownership;

    private SkillPackageRetention(string worldPath)
    {
        _worldPath = Path.GetFullPath(worldPath);
        string ownershipPath = _worldPath + ".skill-packages.owner";
        RejectLink(new FileInfo(ownershipPath));
        _ownership = new(ownershipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    // Both publishers and startup cleanup use this lease, including separate CLI processes.
    // Keep the owner file after closing it so another process cannot lock a different inode.
    public static SkillPackageRetention Acquire(string worldPath) => new(worldPath);

    public static void PruneAfterStartup(string worldPath)
    {
        try
        {
            using var retention = Acquire(worldPath);
            retention.Prune();
        }
        catch (Exception error) when (CleanupFailure(error))
        {
            ServerLog.Console.Warning($"Client package cleanup deferred: {error.Message}");
        }
    }

    public int Prune()
    {
        ObjectDisposedException.ThrowIf(_ownership.SafeFileHandle.IsClosed, this);
        try
        {
            return PruneCore();
        }
        catch (Exception error) when (CleanupFailure(error))
        {
            // Retention is best effort. Never turn a successful publication or startup into a failure.
            ServerLog.Console.Warning($"Client package cleanup deferred: {error.Message}");
            return 0;
        }
    }

    private int PruneCore()
    {
        DirectoryInfo root = new(_worldPath + ".skill-packages");
        RejectLink(root);
        if (!root.Exists)
        {
            return 0;
        }
        HashSet<string> retained = new(StringComparer.OrdinalIgnoreCase);
        void RetainMarker(string suffix)
        {
            string marker = _worldPath + suffix;
            if (SavedFileExists(marker))
            {
                retained.Add(File.ReadAllText(marker));
            }
        }
        RetainMarker(".skills-active");
        bool modsOwnCatalog = false;
        if (SavedFileExists(ModDbContext.FilePath(_worldPath)))
        {
            using ModDbContext db = new(_worldPath);
            db.Database.SetConnectionString(ReadOnlyConnection(ModDbContext.FilePath(_worldPath)));
            var state = db.Set<SavedModConfiguration>().Single();
            modsOwnCatalog = state.Active is not null || state.Activating;
            foreach (string? id in new[] { state.Active, state.Previous, state.Pending })
            {
                if (id is not null)
                {
                    retained.Add(id);
                }
            }
        }
        if (!modsOwnCatalog)
        {
            RetainMarker(".skills-previous");
        }
        if (SavedFileExists(SkillEditorDbContext.FilePath(_worldPath)))
        {
            using SkillEditorDbContext db = new(_worldPath);
            db.Database.SetConnectionString(ReadOnlyConnection(SkillEditorDbContext.FilePath(_worldPath)));
            if (db.Set<SavedSkillEditorState>().Single().PendingPackage is { } pending)
            {
                retained.Add(pending);
            }
        }
        // Resolve every reference before deleting anything. Damaged state must preserve all packages.
        foreach (string id in retained)
        {
            SkillPackageActivation.ValidateId(id);
            DirectoryInfo directory = new(Path.Combine(root.FullName, id));
            RejectLink(directory);
            if (!directory.Exists)
            {
                throw new InvalidDataException("A retained client package directory is missing.");
            }
        }
        int removed = 0;
        foreach (var directory in root.GetDirectories())
        {
            if (retained.Contains(directory.Name) || !OwnedDirectory(directory.Name))
            {
                continue;
            }
            try
            {
                CheckTree(directory);
                directory.Delete(recursive: true);
                removed++;
            }
            catch (Exception error) when (CleanupFailure(error))
            {
                ServerLog.Console.Warning($"Client package cleanup skipped {directory.Name}: {error.Message}");
            }
        }
        if (removed > 0)
        {
            ServerLog.Console.Information($"Removed {removed} obsolete client package/build directories.");
        }
        return removed;
    }

    private static bool OwnedDirectory(string name)
    {
        if (name.Length == 64 && name.All(char.IsAsciiHexDigit))
        {
            return true;
        }
        foreach (string prefix in new[] { ".build-", ".mod-build-" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _))
            {
                return true;
            }
        }
        return false;
    }

    private static void CheckTree(DirectoryInfo directory)
    {
        RejectLink(directory);
        foreach (var entry in directory.GetFileSystemInfos())
        {
            RejectLink(entry);
            if (entry is DirectoryInfo child)
            {
                CheckTree(child);
            }
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException("Client package cleanup does not follow filesystem links.");
        }
    }

    private static bool CleanupFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or InvalidOperationException;

    private static string ReadOnlyConnection(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

    private static bool SavedFileExists(string path)
    {
        try
        {
            // File.Exists also returns false on access failures. Only absence may mean no saved reference.
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Client package cleanup does not follow saved-state links.");
            }
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public void Dispose() => _ownership.Dispose();
}
