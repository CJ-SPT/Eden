using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eden.Infrastructure.Registration;
using Eden.Modding;
using Eden.World.Modding;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.Server.Modding;

[Injectable(InjectionType.Singleton)]
internal sealed class ModPublisher(WorldDatabase database, ModStore store)
{
    // Changes to the native wrappers or client mod loader must rebuild existing selections.
    private const string ClientCompositionVersion = "2";
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<ModSetPackage> Build(string clientRoot, CancellationToken cancellation = default, Func<Task>? authorize = null)
    {
        await _gate.WaitAsync(cancellation);
        string? temporary = null;
        try
        {
            var (packages, snapshot) = store.Prepare();
            string parent = Path.GetFullPath(database.Path) + ".skill-packages";
            Directory.CreateDirectory(parent);
            temporary = Path.Combine(parent, ".mod-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            string overlay = SkillCatalogProjection.Create(
                database.OriginalCatalogPath,
                Path.Combine(temporary, "catalog.sqlite"),
                snapshot
            );
            string identity =
                ClientCompositionVersion + snapshot.CatalogFingerprint + overlay + string.Join("", packages.Select(p => p.Hash));
            string id = ModPackages.Hash(Encoding.UTF8.GetBytes(identity));
            string final = store.SetDirectory(id);
            if (!Directory.Exists(final))
            {
                await File.WriteAllTextAsync(Path.Combine(temporary, "overlay.json"), overlay, cancellation);
                Directory.CreateDirectory(Path.Combine(temporary, "mods"));
                await File.WriteAllTextAsync(
                    Path.Combine(temporary, "mods", ".order.json"),
                    JsonSerializer.Serialize(packages.Select(value => value.Manifest.Id), ModFormat.Json),
                    cancellation
                );
                foreach (var installed in packages)
                {
                    string destination = Path.Combine(temporary, "mods", installed.Manifest.Id);
                    Directory.CreateDirectory(destination);
                    await File.WriteAllTextAsync(
                        Path.Combine(destination, "manifest.json"),
                        JsonSerializer.Serialize(installed.Manifest, ModFormat.Json),
                        cancellation
                    );
                    foreach (var content in installed.Contents)
                    {
                        string target = Path.Combine(destination, content.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await File.WriteAllBytesAsync(target, content.Value, cancellation);
                    }
                }
                await Compile(clientRoot, temporary, cancellation);
                string code = Path.Combine(temporary, "code.ccp");
                await using var stream = File.OpenRead(code);
                var package = new ModSetPackage(
                    id,
                    snapshot.Generation,
                    Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)),
                    stream.Length,
                    packages.Select(value => new ModDependency(value.Manifest.Id, value.Manifest.Version)).ToArray()
                );
                await File.WriteAllTextAsync(
                    Path.Combine(temporary, "mod-set.json"),
                    JsonSerializer.Serialize(package, ModFormat.Json),
                    cancellation
                );
                await File.WriteAllTextAsync(
                    Path.Combine(temporary, "package.json"),
                    JsonSerializer.Serialize(
                        new SkillClientPackage(id, snapshot.Generation, package.CodeSha256, package.CodeLength),
                        ModFormat.Json
                    ),
                    cancellation
                );
                var skills = snapshot
                    .Skills.Where(value => value.UserCreated || value.IsModified)
                    .Select(value => new Eden.Gameplay.Skills.PublishedSkillDefinition(
                        value.Definition,
                        value.UserCreated,
                        value.Deleted,
                        value.UserCreated || value.Definition.BasePrice != value.Original?.BasePrice
                    ));
                await File.WriteAllTextAsync(
                    Path.Combine(temporary, "skills.json"),
                    JsonSerializer.Serialize(skills, ModFormat.Json),
                    cancellation
                );
                stream.Close();
                List<ModFile> files = [];
                foreach (string path in Directory.GetFiles(temporary, "*", SearchOption.AllDirectories).Order())
                {
                    await using var input = File.OpenRead(path);
                    files.Add(
                        new(
                            Path.GetRelativePath(temporary, path).Replace('\\', '/'),
                            Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation)),
                            input.Length
                        )
                    );
                }
                await File.WriteAllTextAsync(
                    Path.Combine(temporary, "files.json"),
                    JsonSerializer.Serialize(files, ModFormat.Json),
                    cancellation
                );
                await PublishDirectory(temporary, final, cancellation);
                temporary = null;
            }
            store.VerifySet(id);
            var result = store.ReadSet(id);
            if (authorize is not null)
            {
                await authorize();
            }
            store.Stage(result, snapshot.Generation);
            return result;
        }
        finally
        {
            if (temporary is not null)
            {
                Directory.Delete(temporary, recursive: true);
            }
            _gate.Release();
        }
    }

    private static async Task PublishDirectory(string temporary, string final, CancellationToken cancellation)
    {
        const int maximumAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(temporary, final);
                return;
            }
            catch (IOException error)
                when (OperatingSystem.IsWindows()
                    && attempt < maximumAttempts
                    && !Directory.Exists(final)
                    && (error.HResult & 0xFFFF) is 5 or 32 or 33
                )
            {
                // Windows can briefly retain a scan handle on the newly compiled archive.
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellation);
            }
        }
    }

    private static async Task Compile(string clientRoot, string directory, CancellationToken cancellation)
    {
        string python = Environment.GetEnvironmentVariable("EDEN_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        ProcessStartInfo start = new(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            string argument in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "skill-tools", "Build-SkillClient.py"),
                "--client",
                Path.GetFullPath(clientRoot),
                "--overlay",
                Path.Combine(directory, "overlay.json"),
                "--output",
                Path.Combine(directory, "code.ccp"),
                "--mods",
                Path.Combine(directory, "mods"),
            }
        )
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new IOException("The mod client compiler could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        await stdout;
        string error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException("Mod client compilation failed: " + error);
        }
    }
}
