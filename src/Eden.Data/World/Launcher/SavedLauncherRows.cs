namespace Eden.Data.World.Launcher;

internal sealed record SavedLauncherCredential(long AccountId)
{
    public string NormalizedName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public long Roles { get; set; }
}

internal sealed record SavedLauncherSession(string Id)
{
    public long AccountId { get; set; }
    public string AccessHash { get; set; } = "";
    public long AccessExpires { get; set; }
    public long Expires { get; set; }
    public bool Revoked { get; set; }
}

internal sealed record SavedLauncherRefresh(string Hash, string SessionId)
{
    public bool Used { get; set; }
}

internal sealed record SavedLauncherTicket(string Hash, string SessionId, long Expires);
