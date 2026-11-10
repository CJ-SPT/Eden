using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Eden.Gameplay.Skills;
using Eden.Modding;
using Eden.Server.Modding;
using Eden.World.Modding;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Administration;

public sealed class ModFoundationTests
{
    private const string Fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static ModManifest Manifest(string id) =>
        new()
        {
            Id = id,
            Name = id,
            CatalogFingerprint = Fingerprint,
        };

    [Fact]
    public void PackagesRoundTripDeterministicallyAndRejectTamperedContents()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var manifest = Manifest("test.example");
            Dictionary<string, byte[]> files = new() { ["client/main.py"] = Encoding.UTF8.GetBytes("def register(api): pass\n") };
            string first = Path.Combine(root, "one.edenmod");
            string second = Path.Combine(root, "two.edenmod");
            ModPackages.Write(first, manifest, files);
            ModPackages.Write(second, manifest, files);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
            var loaded = ModPackages.Read(first);
            Assert.Equal(manifest.Id, loaded.Manifest.Id);
            Assert.Equal(files["client/main.py"], loaded.Contents["client/main.py"]);
            using (var archive = ZipFile.Open(first, ZipArchiveMode.Update))
            {
                archive.GetEntry("client/main.py")!.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("client/main.py").Open());
                writer.Write("changed");
            }
            Assert.Throws<InvalidDataException>(() => ModPackages.Read(first));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [PinnedSkillClientFact]
    public async Task ComposedSetsVerifyActivateAndRecoverInterruptedActivation()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        var store = new ModStore(database);
        string client = Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!;
        var published = await new ModPublisher(database, store).Build(client);
        Assert.Null(store.Status().Active);
        Assert.Equal(published.Id, store.Status().Pending!.Id);
        Assert.True(store.BeginActivation());
        Assert.Throws<InvalidDataException>(() => new ModStore(new WorldDatabase(world.Path)).BeginActivation());
        Assert.Null(Eden.World.Skills.SkillPackageActivation.Active(world.Path));
        Assert.True(store.BeginActivation());
        store.CompleteActivation();
        Assert.Equal(published.Id, new ModStore(new WorldDatabase(world.Path)).Status().Active!.Id);
        Assert.False(store.BeginActivation());
        File.AppendAllText(Path.Combine(store.SetDirectory(published.Id), "code.ccp"), "corrupt");
        Assert.Throws<InvalidDataException>(() => store.VerifySet(published.Id));
    }

    [Fact]
    public void MigratedProjectsPreserveLegacyIdsAndHistoryAndRejectAnotherWorld()
    {
        using TestWorld world = new();
        var database = new WorldDatabase(world.Path);
        var editor = new SkillEditorStore(database, TimeProvider.System);
        var reference = editor.Read();
        var draft = reference.Skills.First().Definition with { TypeId = 0, Name = "Legacy skill", Effects = [], Attributes = [] };
        var created = editor.Create(reference.Generation, draft, "Migration test");
        editor.Save(editor.Read().Generation, created.Definition with { Name = "Updated legacy skill" }, "Migration test");
        var store = new ModStore(database);
        var exported = store.ExportLegacy("test.migrated");
        Assert.Equal(2, exported.History.Count);
        Assert.Equal(created.Definition.TypeId, Assert.Single(exported.LegacyIdentities!.Skills).TypeId);
        var package = new ModPackage(
            exported.Manifest,
            "A",
            new Dictionary<string, byte[]>
            {
                ["skills.json"] = JsonSerializer.SerializeToUtf8Bytes(exported.Skills, ModFormat.Json),
                ["legacy-identities.json"] = JsonSerializer.SerializeToUtf8Bytes(exported.LegacyIdentities, ModFormat.Json),
            }
        );
        var composed = store.Compose([package]);
        Assert.Equal(created.Definition.TypeId, Assert.Single(composed.Skills.Where(value => value.UserCreated)).Definition.TypeId);
        Assert.Equal(
            created.Definition.TypeId,
            new ModStore(new WorldDatabase(world.Path)).ResolveSkill("test.migrated:skill-" + created.Definition.TypeId)
        );
        using TestWorld different = new();
        Assert.Throws<InvalidDataException>(() => new ModStore(new WorldDatabase(different.Path)).Compose([package]));
        Assert.Throws<InvalidDataException>(() => store.Prepare());
    }

    [Fact]
    public void ExplicitNullManifestCollectionsAreRejectedBeforeLoadingFiles()
    {
        Assert.Throws<InvalidDataException>(() => ModPackages.Validate(Manifest("test.invalid") with { Files = null! }));
        Assert.Throws<InvalidDataException>(() => ModPackages.Validate(Manifest("test.invalid") with { Dependencies = null! }));
        Assert.Throws<InvalidDataException>(() => ModPackages.Validate(Manifest("test.invalid") with { CatalogFingerprint = null! }));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a\\b")]
    [InlineData("C:/escape")]
    [InlineData("a/../escape")]
    [InlineData("a./file")]
    public void UnsafePathsAreRejected(string path) => Assert.Throws<InvalidDataException>(() => ModPackages.RelativePath(path));

    [Fact]
    public void ResolutionOrdersDependenciesAndRejectsMissingVersionsCyclesAndDuplicateIdentities()
    {
        var a = new ModPackage(Manifest("test.a"), "A", new Dictionary<string, byte[]>());
        var b = new ModPackage(Manifest("test.b") with { Dependencies = [new("test.a", "1.0.0")] }, "B", new Dictionary<string, byte[]>());
        Assert.Equal(["test.a", "test.b"], ModPackages.Resolve([b, a], Fingerprint).Select(value => value.Manifest.Id));
        Assert.Throws<InvalidDataException>(() => ModPackages.Resolve([b], Fingerprint));
        Assert.Throws<InvalidDataException>(() => ModPackages.Resolve([a, a], Fingerprint));
        Assert.Throws<InvalidDataException>(() =>
            ModPackages.Resolve([a, b with { Manifest = b.Manifest with { Dependencies = [new("test.a", "2.0.0")] } }], Fingerprint)
        );
        Assert.Throws<InvalidDataException>(() =>
            ModPackages.Resolve([b, a with { Manifest = a.Manifest with { Dependencies = [new("test.b", "1.0.0")] } }], Fingerprint)
        );
        Assert.Throws<InvalidDataException>(() => ModPackages.Resolve([a], new string('B', 64)));
    }

    [Fact]
    public void CompositionRetainsPermanentIdsRetiresRemovedSkillsAndPersistsNamespacedState()
    {
        using TestWorld world = new();
        var store = new ModStore(new WorldDatabase(world.Path));
        var reference = store.Reference();
        var definition = reference.Skills.First() with { TypeId = 0, Name = "Custom mod skill", Effects = [], Attributes = [] };
        var package = new ModPackage(
            Manifest("test.skills") with
            {
                CatalogFingerprint = reference.CatalogFingerprint,
            },
            "A",
            new Dictionary<string, byte[]>
            {
                ["skills.json"] = JsonSerializer.SerializeToUtf8Bytes(new[] { new ModSkill("custom", definition) }, ModFormat.Json),
            }
        );
        var first = store.Compose([package]);
        var custom = Assert.Single(first.Skills.Where(value => value.UserCreated));
        Assert.True(custom.Definition.TypeId >= SkillEditingRules.FirstCustomTypeId);
        var reopened = new ModStore(new WorldDatabase(world.Path));
        Assert.Equal(custom.Definition.TypeId, reopened.ResolveSkill("test.skills:custom"));
        Assert.True(Assert.Single(reopened.Compose([]).Skills.Where(value => value.UserCreated)).Deleted);
        Assert.Equal(
            custom.Definition.TypeId,
            Assert.Single(reopened.Compose([package]).Skills.Where(value => value.UserCreated)).Definition.TypeId
        );
        store.WriteState("test.skills", "value", "{\"count\":2}");
        Assert.Equal("{\"count\":2}", reopened.ReadState("test.skills", "value"));
        Assert.Null(reopened.ReadState("test.other", "value"));
    }

    [Fact]
    public void OriginalOverridesMustBeExplicitAndOriginalSkillsCannotBeRetired()
    {
        using TestWorld world = new();
        var store = new ModStore(new WorldDatabase(world.Path));
        var reference = store.Reference();
        var definition = reference.Skills.First();
        string target = "catalog:" + definition.TypeId;
        var skill = new ModSkill("override", definition with { Name = "Renamed" }) { Replaces = target };
        ModPackage Package(ModManifest manifest, ModSkill payload) =>
            new(
                manifest,
                "A",
                new Dictionary<string, byte[]> { ["skills.json"] = JsonSerializer.SerializeToUtf8Bytes(new[] { payload }, ModFormat.Json) }
            );
        var manifest = Manifest("test.override") with { CatalogFingerprint = reference.CatalogFingerprint };
        Assert.Throws<InvalidDataException>(() => store.Compose([Package(manifest, skill)]));
        var valid = manifest with { Overrides = [target] };
        Assert.Equal(
            "Renamed",
            store.Compose([Package(valid, skill)]).Skills.Single(value => value.Definition.TypeId == definition.TypeId).Definition.Name
        );
        Assert.Throws<InvalidDataException>(() => store.Compose([Package(valid, skill with { Retired = true })]));
    }
}
