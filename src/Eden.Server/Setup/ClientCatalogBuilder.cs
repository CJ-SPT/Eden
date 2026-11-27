using System.ComponentModel;
using System.Diagnostics;
using Eden.Gameplay.Content;

namespace Eden.Server.Setup;

internal static class ClientCatalogBuilder
{
    internal static async Task BuildAsync(string clientRoot, string? archivePath, string destination, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidDataException(
                "Building the catalog from the EVE client requires Windows. Supply a prepared catalog with --catalog."
            );
        }

        clientRoot = Path.GetFullPath(clientRoot);
        string client = Path.Combine(clientRoot, "tq");
        if (
            !Directory.Exists(Path.Combine(clientRoot, "ResFiles"))
            || !(File.Exists(Path.Combine(client, "code.ccp")) || File.Exists(Path.Combine(clientRoot, ".eden-original-code.ccp")))
            || !File.Exists(Path.Combine(client, "resfileindex.txt"))
            || !File.Exists(Path.Combine(client, "bin64", "python27.dll"))
        )
        {
            throw new InvalidDataException(
                $"Select the original EVE build {BuildContentPolicy.PinnedBuild} folder containing tq and the ResFiles cache."
            );
        }

        if (archivePath is not null && !File.Exists(archivePath))
        {
            throw new FileNotFoundException("The supplied EVE static data archive was not found.", archivePath);
        }

        string script = Path.Combine(AppContext.BaseDirectory, "catalog-tools", "build.py");
        if (!File.Exists(script))
        {
            throw new FileNotFoundException("Catalog authoring tools are missing from this Eden build.", script);
        }

        string host = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the running Eden executable.");
        string? python = Environment.GetEnvironmentVariable("EDEN_PYTHON");
        ProcessStartInfo start = new(string.IsNullOrWhiteSpace(python) ? "python" : python) { UseShellExecute = false };
        start.Environment["EDEN_NO_PAUSE_ON_ERROR"] = "1";
        foreach (string argument in new[] { "-E", "-s", "-B", script, clientRoot, "--output", Path.GetFullPath(destination) })
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("--server-executable");
        start.ArgumentList.Add(host);
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("--server-assembly");
            start.ArgumentList.Add(typeof(ClientCatalogBuilder).Assembly.Location);
        }

        if (archivePath is not null)
        {
            start.ArgumentList.Add("--archive");
            start.ArgumentList.Add(Path.GetFullPath(archivePath));
        }

        using Process process = new() { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            throw new InvalidDataException(
                "Catalog authoring requires 64-bit Python 3.11 or newer. Install it or set EDEN_PYTHON to its executable.",
                error
            );
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited before it could be stopped.
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"Catalog authoring failed with exit code {process.ExitCode}. See the source or verification error above."
            );
        }
    }
}
