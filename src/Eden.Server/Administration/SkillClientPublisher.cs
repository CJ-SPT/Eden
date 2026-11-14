using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eden.Infrastructure.Registration;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.Server.Administration;

internal sealed record SkillPublicationStatus(SkillClientPackage? Active, SkillClientPackage? Pending, string? ClientRoot);

[Injectable(InjectionType.Singleton)]
internal sealed class SkillClientPublisher(WorldDatabase database, SkillEditorStore editor)
{
    private readonly SemaphoreSlim _gate = new(1);

    internal SkillPublicationStatus Status()
    {
        SkillClientPackage? pending = null;
        string? clientRoot = Environment.GetEnvironmentVariable("EDEN_CLIENT_ROOT");
        string sidecar = SkillEditorDbContext.FilePath(database.Path);
        if (File.Exists(sidecar))
        {
            using SkillEditorDbContext db = new(database.Path);
            var state = db.Set<Eden.Data.World.Skills.SavedSkillEditorState>().Single();
            string? id = state.PendingPackage;
            clientRoot = state.ClientRoot ?? clientRoot;
            pending = id is null ? null : SkillPackageActivation.ReadPackage(database.Path, id);
        }
        return new(SkillPackageActivation.Active(database.Path), pending, clientRoot);
    }

    internal async Task<SkillClientPackage> Publish(
        long generation,
        string clientRoot,
        CancellationToken cancellation = default,
        Func<Task>? authorize = null
    )
    {
        await _gate.WaitAsync(cancellation);
        string? temporary = null;
        try
        {
            var snapshot = editor.Read();
            if (snapshot.Generation != generation)
            {
                throw new InvalidOperationException("Skills changed in another editor. Reload before publishing.");
            }
            string parent = Path.GetFullPath(database.Path) + ".skill-packages";
            Directory.CreateDirectory(parent);
            temporary = Path.Combine(parent, ".build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            string overlay = SkillCatalogProjection.Create(
                database.OriginalCatalogPath,
                Path.Combine(temporary, "catalog.sqlite"),
                snapshot
            );
            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.CatalogFingerprint + generation + overlay)));
            string folder = SkillPackageActivation.DirectoryPath(database.Path, id);
            if (!Directory.Exists(folder))
            {
                string input = Path.Combine(temporary, "overlay.json");
                await File.WriteAllTextAsync(input, overlay, cancellation);
                string code = Path.Combine(temporary, "code.ccp");
                string python = Environment.GetEnvironmentVariable("EDEN_PYTHON") ?? (OperatingSystem.IsWindows() ? "python" : "python3");
                var start = new ProcessStartInfo(python)
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
                        input,
                        "--output",
                        code,
                    }
                )
                {
                    start.ArgumentList.Add(argument);
                }
                using var process = Process.Start(start) ?? throw new IOException("The skill package compiler could not start.");
                var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
                var stderr = process.StandardError.ReadToEndAsync(cancellation);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromMinutes(3));
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    await process.WaitForExitAsync(CancellationToken.None);
                    throw;
                }
                await stdout;
                string errors = await stderr;
                if (process.ExitCode != 0)
                {
                    throw new InvalidDataException(
                        "Skill packaging failed. Select an unchanged build-3662168 client and check Python setup. " + errors
                    );
                }
                await using var stream = File.OpenRead(code);
                string sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
                var package = new SkillClientPackage(id, generation, sha, stream.Length);
                await File.WriteAllTextAsync(
                    Path.Combine(temporary, "package.json"),
                    JsonSerializer.Serialize(package, SkillEditorStore.Json),
                    cancellation
                );
                await File.WriteAllTextAsync(
                    Path.Combine(temporary, "skills.json"),
                    JsonSerializer.Serialize(
                        snapshot
                            .Skills.Where(entry => entry.UserCreated || entry.IsModified)
                            .Select(entry => new Eden.Gameplay.Skills.PublishedSkillDefinition(
                                entry.Definition,
                                entry.UserCreated,
                                entry.Deleted,
                                entry.UserCreated || entry.Definition.BasePrice != entry.Original?.BasePrice
                            )),
                        SkillEditorStore.Json
                    ),
                    cancellation
                );
                stream.Close();
                Directory.Move(temporary, folder);
                temporary = null;
            }
            if (authorize is not null)
            {
                await authorize();
            }
            editor.StagePackage(generation, id, Path.GetFullPath(clientRoot));
            return SkillPackageActivation.ReadPackage(database.Path, id);
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidDataException(
                "Skill packaging needs 64-bit Python 3.11 or newer on the server. Set EDEN_PYTHON if it is not on PATH.",
                error
            );
        }
        finally
        {
            if (temporary is not null && Directory.Exists(temporary))
            {
                // This path is created directly under the package folder, never from request input.
                Directory.Delete(temporary, recursive: true);
            }
            _gate.Release();
        }
    }

    internal string ActiveCodePath(string id)
    {
        if (Status().Active?.Id != id)
        {
            throw new InvalidDataException("This skill package is no longer active. Refresh the server information.");
        }
        return Path.Combine(SkillPackageActivation.DirectoryPath(database.Path, id), "code.ccp");
    }
}
