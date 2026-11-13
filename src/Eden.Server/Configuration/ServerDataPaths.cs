namespace Eden.Server.Configuration;

internal static class ServerDataPaths
{
    internal static string WorldPath(string? world, string? dataDirectory, string workingDirectory, string applicationDirectory)
    {
        if (world is not null)
        {
            return Path.GetFullPath(world, workingDirectory);
        }
        // dotnet run starts in the web project directory. Keep source data outside its build output.
        string serverDirectory = File.Exists(Path.Combine(workingDirectory, "Eden.Server.csproj"))
            ? workingDirectory
            : applicationDirectory;
        string directory = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDirectory) ? "data" : dataDirectory, serverDirectory);
        return Path.Combine(directory, "world.sqlite");
    }
}
