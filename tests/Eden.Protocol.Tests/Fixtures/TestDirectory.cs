namespace Eden.Protocol.Tests.Fixtures;

internal static class TestDirectory
{
    internal static readonly string Root = FindRoot();

    public static DirectoryInfo Create(string prefix)
    {
        Directory.CreateDirectory(Root);
        return Directory.CreateDirectory(Path.Combine(Root, prefix + Guid.NewGuid().ToString("N")));
    }

    private static string FindRoot()
    {
        if (Environment.GetEnvironmentVariable("EDEN_TEST_TEMP_DIRECTORY") is { Length: > 0 } configured)
        {
            return Path.GetFullPath(configured);
        }
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Eden.slnx")))
            {
                // Keep SQLite scratch writes off the operating system's temporary drive.
                return Path.Combine(directory.FullName, "artifacts", "test-data");
            }
        }
        return Path.Combine(Path.GetTempPath(), "Eden", "test-data");
    }
}
