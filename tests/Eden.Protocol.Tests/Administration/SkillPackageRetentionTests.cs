using System.Diagnostics;
using Eden.Data.World.Modding;
using Eden.Data.World.Skills;
using Eden.Server.Administration;
using Eden.Server.Modding;
using Eden.World.Modding;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Administration;

public sealed class SkillPackageRetentionTests
{
    [Fact]
    public void PruningPreservesAllSavedReferencesAndRemovesOnlyOwnedObsoleteDirectories()
    {
        using PackageFiles files = new();
        string marker = files.Package('A');
        string active = files.Package('B');
        string previous = files.Package('C');
        string pending = files.Package('D');
        string skillPending = files.Package('E');
        string obsolete = files.Package('F');
        files.ModState(active, previous, pending);
        files.SkillState(skillPending);
        File.WriteAllText(files.World + ".skills-active", marker);
        string build = files.Directory(".build-" + Guid.NewGuid().ToString("N"));
        string modBuild = files.Directory(".mod-build-" + Guid.NewGuid().ToString("N"));
        string unknown = files.Directory("operator-backup");
        string malformed = files.Directory(".build-not-a-guid");
        File.WriteAllText(Path.Combine(files.Root, "operator-note.txt"), "keep");
        File.WriteAllText(files.World, "saved world");
        File.WriteAllText(files.World + ".previous", "previous world");

        using var retention = SkillPackageRetention.Acquire(files.World);
        Assert.Equal(3, retention.Prune());
        foreach (string id in new[] { marker, active, previous, pending, skillPending })
        {
            Assert.Equal("payload", File.ReadAllText(Path.Combine(files.Root, id, "catalog.sqlite")));
        }
        Assert.False(Directory.Exists(Path.Combine(files.Root, obsolete)));
        Assert.False(Directory.Exists(build));
        Assert.False(Directory.Exists(modBuild));
        Assert.True(Directory.Exists(unknown));
        Assert.True(Directory.Exists(malformed));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(files.Root, "operator-note.txt")));
        Assert.Equal("saved world", File.ReadAllText(files.World));
        Assert.Equal("previous world", File.ReadAllText(files.World + ".previous"));
        Assert.Equal(0, retention.Prune());
    }

    [Fact]
    public void StartupRetainsLegacyRollbackAndReopenedPendingState()
    {
        using PackageFiles files = new();
        string active = files.Package('A');
        string previous = files.Package('B');
        string pending = files.Package('C');
        string obsolete = files.Package('D');
        File.WriteAllText(files.World + ".skills-active", active);
        File.WriteAllText(files.World + ".skills-previous", previous);
        files.SkillState(pending);
        SkillPackageRetention.PruneAfterStartup(files.World);
        Assert.True(Directory.Exists(Path.Combine(files.Root, previous)));
        Assert.True(Directory.Exists(Path.Combine(files.Root, pending)));
        Assert.False(Directory.Exists(Path.Combine(files.Root, obsolete)));

        // Once mods own activation, their durable Previous replaces legacy rollback history.
        files.ModState(active, pending, null);
        SkillPackageRetention.PruneAfterStartup(files.World);
        Assert.True(Directory.Exists(Path.Combine(files.Root, active)));
        Assert.True(Directory.Exists(Path.Combine(files.Root, pending)));
        Assert.False(Directory.Exists(Path.Combine(files.Root, previous)));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("missing")]
    public void InvalidOrMissingReferencesDeferAllDeletion(string damage)
    {
        using PackageFiles files = new();
        string obsolete = files.Package('A');
        File.WriteAllText(files.World + ".skills-active", damage == "missing" ? new string('B', 64) : damage);
        using var retention = SkillPackageRetention.Acquire(files.World);
        Assert.Equal(0, retention.Prune());
        Assert.True(Directory.Exists(Path.Combine(files.Root, obsolete)));
    }

    [Fact]
    public void UnreadableSavedStateDefersAllDeletion()
    {
        using PackageFiles files = new();
        string obsolete = files.Package('A');
        File.WriteAllText(files.World + ".mods.sqlite", "damaged database");
        using var retention = SkillPackageRetention.Acquire(files.World);
        Assert.Equal(0, retention.Prune());
        Assert.True(Directory.Exists(Path.Combine(files.Root, obsolete)));
    }

    [Fact]
    public void RepeatedPublicationAndRestartKeepOnlyCurrentPendingAndRollbackGenerations()
    {
        using PackageFiles files = new();
        string active = files.Package('A');
        string previous = files.Package('B');
        foreach (char identity in new[] { 'C', 'D', 'E', 'F' })
        {
            string pending = files.Package(identity);
            files.ModState(active, previous, pending);
            File.WriteAllText(files.World + ".skills-active", active);
            SkillPackageRetention.PruneAfterStartup(files.World);
            Assert.Equal(
                new[] { active, previous, pending }.Order(),
                System.IO.Directory.GetDirectories(files.Root).Select(Path.GetFileName).Order()
            );
        }
        string next = new('F', 64);
        files.ModState(next, active, null);
        File.WriteAllText(files.World + ".skills-active", next);
        SkillPackageRetention.PruneAfterStartup(files.World);
        Assert.Equal(new[] { active, next }.Order(), System.IO.Directory.GetDirectories(files.Root).Select(Path.GetFileName).Order());
    }

    [PinnedSkillClientFact]
    public async Task FailedPublicationPrunesUnstagedOutputAndReleasesBothPublisherGates()
    {
        using TestWorld world = new();
        WorldDatabase database = new(world.Path);
        ModStore store = new(database);
        ModPublisher mods = new(database, store);
        string client = Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!;
        Func<Task> reject = () => throw new InvalidOperationException("Authorization expired.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => mods.Build(client, authorize: reject));
        Assert.Empty(System.IO.Directory.GetDirectories(world.Path + ".skill-packages"));
        var mod = await mods.Build(client);
        SkillEditorStore editor = new(database, TimeProvider.System);
        SkillClientPublisher skills = new(database, editor);
        long generation = editor.Read().Generation;
        await Assert.ThrowsAsync<InvalidOperationException>(() => skills.Publish(generation, client, authorize: reject));
        Assert.Equal(mod.Id, store.Status().Pending!.Id);
        Assert.Single(System.IO.Directory.GetDirectories(world.Path + ".skill-packages"));
        var skill = await skills.Publish(generation, client);
        Assert.Equal(skill.Id, skills.Status().Pending!.Id);
        Assert.True(System.IO.Directory.Exists(store.SetDirectory(mod.Id)));
    }

    [Fact]
    public async Task PublisherOwnershipProtectsInProgressBuildsAndDoesNotPoisonPublisherGates()
    {
        using PackageFiles files = new();
        var database = new WorldDatabase(files.World);
        ModPublisher mods = new(database, new(database));
        SkillClientPublisher skills = new(database, new(database, TimeProvider.System));
        string build = files.Directory(".build-" + Guid.NewGuid().ToString("N"));
        using (SkillPackageRetention.Acquire(files.World))
        {
            await Assert.ThrowsAsync<IOException>(() => mods.Build("unused"));
            await Assert.ThrowsAsync<IOException>(() => skills.Publish(0, "unused"));
            SkillPackageRetention.PruneAfterStartup(files.World);
            Assert.True(Directory.Exists(build));
        }
        // Reacquisition works even though the owner file remains on disk after disposal.
        using (SkillPackageRetention.Acquire(files.World))
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => mods.Build("unused", timeout.Token));
            await Assert.ThrowsAsync<IOException>(() => skills.Publish(0, "unused", timeout.Token));
        }
        SkillPackageRetention.PruneAfterStartup(files.World);
        Assert.False(Directory.Exists(build));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupNeverFollowsRootOrNestedLinks(bool rootLink)
    {
        using PackageFiles files = new();
        using PackageFiles outside = new();
        string outsidePackage = outside.Package('A');
        string outsidePath = Path.Combine(outside.Root, outsidePackage);
        List<string> links = [];
        try
        {
            if (rootLink)
            {
                Directory.Delete(files.Root);
                links.Add(files.Root);
                CreateLink(files.Root, outside.Root);
            }
            else
            {
                string orphan = files.Directory(new string('B', 64));
                links.Add(Path.Combine(orphan, "external"));
                CreateLink(links[^1], outsidePath);
                links.Add(Path.Combine(files.Root, new string('C', 64)));
                CreateLink(links[^1], outsidePath);
            }
            using var retention = SkillPackageRetention.Acquire(files.World);
            Assert.Equal(0, retention.Prune());
            Assert.Equal("payload", File.ReadAllText(Path.Combine(outsidePath, "catalog.sqlite")));
        }
        finally
        {
            // Unlink junctions before their targets are disposed; never recurse through them.
            foreach (string link in links)
            {
                if (new DirectoryInfo(link).LinkTarget is not null)
                {
                    Directory.Delete(link);
                }
            }
        }
    }

    private static void CreateLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }
        // Directory junctions exercise reparse-point handling without requiring Windows symlink privileges.
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(
            "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"
                + path.Replace("'", "''")
                + "' -Target '"
                + target.Replace("'", "''")
                + "' | Out-Null"
        );
        using var process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private sealed class PackageFiles : IDisposable
    {
        private readonly string _directory = System.IO.Directory.CreateTempSubdirectory("eden-retention-").FullName;
        internal string World => Path.Combine(_directory, "world.sqlite");
        internal string Root => World + ".skill-packages";

        internal PackageFiles()
        {
            System.IO.Directory.CreateDirectory(Root);
        }

        internal string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

        internal string Package(char identity)
        {
            string id = new(identity, 64);
            File.WriteAllText(Path.Combine(Directory(id), "catalog.sqlite"), "payload");
            return id;
        }

        internal void ModState(string? active, string? previous, string? pending)
        {
            using ModDbContext db = new(World);
            db.Database.EnsureCreated();
            var state = db.Set<SavedModConfiguration>().SingleOrDefault();
            if (state is null)
            {
                state = new();
                db.Add(state);
            }
            state.Active = active;
            state.Previous = previous;
            state.Pending = pending;
            db.SaveChanges();
        }

        internal void SkillState(string? pending)
        {
            using SkillEditorDbContext db = new(World);
            db.Database.EnsureCreated();
            db.Add(new SavedSkillEditorState { PendingPackage = pending, CatalogFingerprint = "fixture" });
            db.SaveChanges();
        }

        public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);
    }
}
