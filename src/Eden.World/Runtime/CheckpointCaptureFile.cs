namespace Eden.World.Runtime;

internal static class CheckpointCaptureFile
{
    private const string CaptureSuffix = ".checkpoint-capture-";
    private const string StagingSuffix = ".save-";
    private const string Journal = "-journal";

    internal static string Create(string worldPath)
    {
        string path = worldPath + CaptureSuffix + Guid.NewGuid().ToString("N");
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

    internal static string StagingPath(string worldPath) => worldPath + StagingSuffix + Guid.NewGuid().ToString("N");

    internal static void Reclaim(string worldPath)
    {
        string worldName = Path.GetFileName(worldPath);
        string capturePrefix = worldName + CaptureSuffix;
        string stagingPrefix = worldName + StagingSuffix;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        EnumerationOptions options = new() { AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(worldPath)!, "*", options))
        {
            var name = Path.GetFileName(path).AsSpan();
            if (name.StartsWith(capturePrefix, comparison))
            {
                name = name[capturePrefix.Length..];
            }
            else if (name.StartsWith(stagingPrefix, comparison))
            {
                name = name[stagingPrefix.Length..];
            }
            else
            {
                continue;
            }
            if (name.EndsWith(Journal, comparison))
            {
                name = name[..^Journal.Length];
            }
            if (name.Length == 32 && Guid.TryParseExact(name, "N", out _))
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
