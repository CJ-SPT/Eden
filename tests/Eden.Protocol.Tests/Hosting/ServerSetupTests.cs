using System.Diagnostics;
using Eden.Server;

namespace Eden.Protocol.Tests.Hosting;

public sealed class ServerSetupTests
{
    [Fact]
    public async Task InvalidCatalogNeverPublishesOrCreatesTheWorld()
    {
        var directory = Directory.CreateTempSubdirectory("eden-setup-");
        try
        {
            string world = Path.Combine(directory.FullName, "Eden's data", "custom.sqlite");
            string source = Path.Combine(directory.FullName, "invalid catalog.sqlite");
            File.WriteAllText(source, "invalid catalog");

            var result = await RunSetup("--world", world, "--catalog", source, "--administrator", "admin");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("not a usable Eden catalog", result.Error);
            Assert.Equal("invalid catalog", File.ReadAllText(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(world)!));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SetupPreservesExistingCatalogAndUsesTheWorldOverride()
    {
        var directory = Directory.CreateTempSubdirectory("eden-setup-");
        try
        {
            string world = Path.Combine(directory.FullName, "custom.sqlite");
            string catalog = Path.Combine(directory.FullName, "catalog.sqlite");
            File.WriteAllText(catalog, "existing catalog");

            var result = await RunSetup("--world", world, "--catalog", Path.Combine(directory.FullName, "new.sqlite"));

            Assert.Equal(1, result.ExitCode);
            Assert.Contains($"Catalog already exists at {catalog}", result.Error);
            Assert.Equal("existing catalog", File.ReadAllText(catalog));
            Assert.Equal(new[] { catalog }, Directory.EnumerateFileSystemEntries(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("--catalog", "requires a value")]
    [InlineData("--admin-password-stdin", "requires --administrator")]
    [InlineData("--sde-archive=source.zip", "Unknown setup option")]
    public async Task SetupReportsInvalidOptions(string option, string error)
    {
        var result = await RunSetup(option);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(error, result.Error);
    }

    private static async Task<(int ExitCode, string Error)> RunSetup(params string[] arguments)
    {
        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            ArgumentList = { typeof(ServerServices).Assembly.Location, "--setup" },
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var diagnostics = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.DoesNotContain("listening", await output);
            return (process.ExitCode, await diagnostics);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
