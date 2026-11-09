namespace Eden.Data.World.Skills;

public sealed class SavedSkillEditorState
{
    public int Id { get; set; } = 1;
    public long Generation { get; set; }
    public int NextTypeId { get; set; } = Eden.Gameplay.Skills.SkillEditingRules.FirstCustomTypeId;
    public string CatalogFingerprint { get; set; } = "";
    public string? PendingPackage { get; set; }
    public string? ClientRoot { get; set; }
}

public sealed class SavedSkillEdit
{
    public int TypeId { get; set; }
    public bool UserCreated { get; set; }
    public bool Deleted { get; set; }
    public long Revision { get; set; }
    public string Document { get; set; } = "";
}

public sealed class SavedSkillEditRevision
{
    public long Id { get; set; }
    public int TypeId { get; set; }
    public string Author { get; set; } = "";
    public long CreatedAt { get; set; }
    public string Operation { get; set; } = "";
    public string Document { get; set; } = "";
}
