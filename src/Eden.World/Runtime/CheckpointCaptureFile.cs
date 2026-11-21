namespace Eden.World.Runtime;

internal static class CheckpointCaptureFile
{
    private const string Suffix = ".checkpoint-capture-";
    private const string Journal = "-journal";

    internal static string Create(string worldPath)
    {
        string path = worldPath + Suffix + Guid.NewGuid().ToString("N");
        FileStreamOptions create = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using FileStream file = new(path, create);
        return path;
    }

    internal static void Reclaim(string worldPath)
    {
        string prefix = Path.GetFileName(worldPath) + Suffix;
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(worldPath)!))
        {
            var name = Path.GetFileName(path).AsSpan();
            if (!name.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                continue;
            }
            var identity = name[prefix.Length..];
            if (identity.EndsWith(Journal, StringComparison.Ordinal))
            {
                identity = identity[..^Journal.Length];
            }
            if (identity.Length == 32 && Guid.TryParseExact(identity, "N", out _))
            {
                TryDelete(path);
            }
        }
    }

    internal static void Delete(string path)
    {
        TryDelete(path);
        TryDelete(path + Journal);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next owner can reclaim a leftover scratch file after releasing the handle.
        }
    }
}
