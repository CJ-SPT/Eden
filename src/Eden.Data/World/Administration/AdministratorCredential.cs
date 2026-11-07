namespace Eden.Data.World.Administration;

public sealed record AdministratorCredential(string Name, string PasswordHash, string SecurityStamp);
