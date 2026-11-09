using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Skills;

namespace Eden.Modding;

public static class ModFormat
{
    public const int Version = 1;
    public const int ClientBuild = 3662168;
    public const string ApiVersion = "1.0.0";
    public const int MaximumCommandBytes = 64 * 1024;
    public static JsonSerializerOptions Json { get; } =
        new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };
}

public sealed record ModDependency(string Id, string Version);

public sealed record ModFile(string Path, string Sha256, long Length);

public sealed record ModPlugin(string Assembly, string Type);

public sealed record ModManifest
{
    public int FormatVersion { get; init; } = ModFormat.Version;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string ApiVersion { get; init; } = ModFormat.ApiVersion;
    public int ClientBuild { get; init; } = ModFormat.ClientBuild;
    public string CatalogFingerprint { get; init; } = "";
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Overrides { get; init; } = [];
    public ModPlugin? Server { get; init; }
    public string? ClientEntry { get; init; }
    public IReadOnlyList<ModFile> Files { get; init; } = [];
}

// Logical prerequisite references are resolved to permanent world IDs before gameplay validation.
public sealed record ModPrerequisite(string Skill, int Level);

public sealed record ModSkill(string Id, SkillDefinition Definition)
{
    public string? Replaces { get; init; }
    public IReadOnlyList<ModPrerequisite> Requirements { get; init; } = [];
    public bool Retired { get; init; }
}

public sealed record ModReference(
    int FormatVersion,
    int ClientBuild,
    string CatalogFingerprint,
    IReadOnlyList<SkillDefinition> Skills,
    SkillEditorChoices Choices
);

public sealed record ModPackage(ModManifest Manifest, string Hash, IReadOnlyDictionary<string, byte[]> Contents);

public sealed record ModSetPackage(string Id, long Generation, string CodeSha256, long CodeLength, IReadOnlyList<ModDependency> Mods);

public sealed record ModSetStatus(ModSetPackage? Active, ModSetPackage? Pending, IReadOnlyList<ModDependency> Enabled);
