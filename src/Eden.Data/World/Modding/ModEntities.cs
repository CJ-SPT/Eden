namespace Eden.Data.World.Modding;

public sealed class SavedModIdentity
{
    public string LogicalId { get; set; } = "";
    public int TypeId { get; set; }
    public string Definition { get; set; } = "";
}

public sealed class SavedModState
{
    public string ModId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Json { get; set; } = "null";
}

public sealed class SavedModConfiguration
{
    public int Id { get; set; } = 1;
    public string Enabled { get; set; } = "[]";
    public string? Active { get; set; }
    public string? Pending { get; set; }
    public string? Previous { get; set; }
    public bool Activating { get; set; }
    public long Generation { get; set; }
}
