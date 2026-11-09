using Eden.Gameplay.Skills;

namespace Eden.Modding;

public sealed record ModIdentityAllocation(string LogicalId, int TypeId);

public sealed record ModLegacyIdentities(string WorldIdentity, IReadOnlyList<ModIdentityAllocation> Skills);

public sealed record ModProjectDocument
{
    public int FormatVersion { get; init; } = ModFormat.Version;
    public ModManifest Manifest { get; set; } = new();
    public List<ModSkill> Skills { get; init; } = [];
    public List<SkillEditorRevision> History { get; init; } = [];
    public long Generation { get; set; }
    public int NextTypeId { get; set; } = SkillEditingRules.FirstCustomTypeId;
    public ModLegacyIdentities? LegacyIdentities { get; init; }
}
