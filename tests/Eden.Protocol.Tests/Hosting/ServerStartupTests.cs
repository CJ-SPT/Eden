using System.Diagnostics;
using Eden.Server;

namespace Eden.Protocol.Tests.Hosting;

public sealed class ServerStartupTests
{
    [Fact]
    public async Task MissingCatalogPrintsExpectedPathAndExitsWithRedirectedInput()
    {
        var directory = Directory.CreateTempSubdirectory("eden-startup-");
        try
        {
            string data = Path.Combine(directory.FullName, "Eden's data");
            string world = Path.Combine(data, "world.sqlite");
            string catalog = Path.Combine(data, "catalog.sqlite");
            ProcessStartInfo start = new("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(typeof(ServerServices).Assembly.Location);
            start.ArgumentList.Add("--world");
            start.ArgumentList.Add(world);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            Assert.Equal(1, process.ExitCode);
            string diagnostic = await error;
            Assert.Contains("Eden could not start because its game data catalog (catalog.sqlite) is missing.", diagnostic);
            Assert.Contains($"Expected file: {catalog}", diagnostic);
            Assert.Contains("./tools/Setup-Catalog.ps1 -DevelopmentRoot", diagnostic);
            Assert.Contains($"-Output '{catalog.Replace("'", "''", StringComparison.Ordinal)}'", diagnostic);
            Assert.DoesNotContain("Unhandled exception", diagnostic);
            Assert.DoesNotContain("Press any key", diagnostic);
            Assert.DoesNotContain("listening", await output);
            Assert.False(File.Exists(world));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
