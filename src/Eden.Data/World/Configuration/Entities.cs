namespace Eden.Data.World.Configuration;

public sealed class SavedConfigurationRevision
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public string Author { get; set; } = "";
    public long CreatedAt { get; set; }
    public string Document { get; set; } = "";
}

public sealed class SavedConfigurationState
{
    public int Id { get; set; } = 1;
    public long Generation { get; set; }
    public long ActiveRevisionId { get; set; }
    public long? PendingRevisionId { get; set; }
    public string? ActivationFailure { get; set; }
}
