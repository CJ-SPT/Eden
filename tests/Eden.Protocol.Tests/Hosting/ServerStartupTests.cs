using System.Diagnostics;
using Eden.Server;
using Eden.Server.Administration;

namespace Eden.Protocol.Tests.Hosting;

public sealed class ServerStartupTests
{
    [Theory]
    [InlineData("admin", "A valid password ", "A valid password ", null)]
    [InlineData("admin", "short", "short", "Use an administrator password")]
    [InlineData("admin", "A valid password", "Different password", "Passwords do not match.")]
    [InlineData("admin", "A valid password", null, "Standard input must contain the password and confirmation")]
    [InlineData("invalid name", "A valid password", "A valid password", "Use an administrator name")]
    public async Task AdministratorProvisioningReadsRedirectedPasswordAndConfirmation(
        string name,
        string password,
        string? confirmation,
        string? expectedError
    )
    {
        var directory = Directory.CreateTempSubdirectory("eden-administrator-");
        try
        {
            string world = Path.Combine(directory.FullName, "world.sqlite");
            ProcessStartInfo start = new("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    typeof(ServerServices).Assembly.Location,
                    "--world",
                    world,
                    "--set-admin-password",
                    name,
                    "--admin-password-stdin",
                },
            };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.WriteLineAsync(password);
                if (confirmation is not null)
                {
                    await process.StandardInput.WriteLineAsync(confirmation);
                }
                process.StandardInput.Close();
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

            string stdout = await output;
            string stderr = await error;
            Assert.DoesNotContain("listening", stdout);
            Assert.DoesNotContain(password, stdout);
            Assert.DoesNotContain(password, stderr);
            Assert.DoesNotContain("Unhandled exception", stderr);
            Assert.False(File.Exists(world));
            AdministratorAccounts accounts = new(world);
            if (expectedError is null)
            {
                Assert.Equal(0, process.ExitCode);
                Assert.Contains("Local administrator provisioned", stdout);
                Assert.NotNull(accounts.Authenticate(name, password));
            }
            else
            {
                Assert.Equal(1, process.ExitCode);
                Assert.Contains(expectedError, stderr);
                Assert.False(accounts.Provisioned);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

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
