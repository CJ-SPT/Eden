using Eden.Data.World.Launcher;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private void RegisterLauncherTables()
    {
        Register(
            new EconomyTable<SavedLauncherCredential, SavedLauncherCredential>(
                "LauncherCredential",
                row => new(row.AccountId),
                row => row with { },
                row => row with { },
                ProjectCredentials,
                "AccountId,NormalizedName,PasswordHash,Roles",
                read =>
                    new(read.GetInt64(1))
                    {
                        NormalizedName = read.GetString(2),
                        PasswordHash = read.GetString(3),
                        Roles = read.GetInt64(4),
                    },
                row => new(row.AccountId),
                indexes: [new("NormalizedName", row => row.NormalizedName)],
                primaryColumn: "AccountId",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedLauncherSession, SavedLauncherSession>(
                "LauncherSession",
                row => new(0, Name: row.Id),
                row => row with { },
                row => row with { },
                ProjectSessions,
                "Id,AccountId,AccessHash,AccessExpires,Expires,Revoked",
                read =>
                    new(read.GetString(1))
                    {
                        AccountId = read.GetInt64(2),
                        AccessHash = read.GetString(3),
                        AccessExpires = read.GetInt64(4),
                        Expires = read.GetInt64(5),
                        Revoked = read.GetBoolean(6),
                    },
                row => new(0, Name: row.Id),
                indexes:
                [
                    new("AccountId", row => row.AccountId),
                    new("AccessHash", row => row.AccessHash),
                    new("Revoked", row => row.Revoked),
                ],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedLauncherRefresh, SavedLauncherRefresh>(
                "LauncherRefresh",
                row => new(0, Name: row.Hash),
                row => row with { },
                row => row with { },
                ProjectRefresh,
                "Hash,SessionId,Used",
                read => new(read.GetString(1), read.GetString(2)) { Used = read.GetBoolean(3) },
                row => new(0, Name: row.Hash),
                indexes: [new("SessionId", row => row.SessionId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedLauncherTicket, SavedLauncherTicket>(
                "LauncherTicket",
                row => new(0, Name: row.Hash),
                row => row,
                row => row with { },
                ProjectTickets,
                "Hash,SessionId,Expires",
                read => new(read.GetString(1), read.GetString(2), read.GetInt64(3)),
                row => new(0, Name: row.Hash),
                indexes: [new("SessionId", row => row.SessionId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectCredentials(WorldDbContext world, IReadOnlyList<EconomyChange<SavedLauncherCredential>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO LauncherCredential(AccountId,NormalizedName,PasswordHash,Roles)
                    VALUES({row.AccountId},{row.NormalizedName},{row.PasswordHash},{row.Roles})
                    ON CONFLICT(AccountId) DO UPDATE SET
                        NormalizedName=excluded.NormalizedName,PasswordHash=excluded.PasswordHash,Roles=excluded.Roles
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM LauncherCredential WHERE AccountId={change.Key.Id}");
            }
        }
    }

    private static void ProjectSessions(WorldDbContext world, IReadOnlyList<EconomyChange<SavedLauncherSession>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO LauncherSession(Id,AccountId,AccessHash,AccessExpires,Expires,Revoked)
                    VALUES({row.Id},{row.AccountId},{row.AccessHash},{row.AccessExpires},{row.Expires},{row.Revoked})
                    ON CONFLICT(Id) DO UPDATE SET
                        AccountId=excluded.AccountId,AccessHash=excluded.AccessHash,AccessExpires=excluded.AccessExpires,
                        Expires=excluded.Expires,Revoked=excluded.Revoked
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM LauncherSession WHERE Id={change.Key.Name}");
            }
        }
    }

    private static void ProjectRefresh(WorldDbContext world, IReadOnlyList<EconomyChange<SavedLauncherRefresh>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO LauncherRefresh(Hash,SessionId,Used) VALUES({row.Hash},{row.SessionId},{row.Used})
                    ON CONFLICT(Hash) DO UPDATE SET SessionId=excluded.SessionId,Used=excluded.Used
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM LauncherRefresh WHERE Hash={change.Key.Name}");
            }
        }
    }

    private static void ProjectTickets(WorldDbContext world, IReadOnlyList<EconomyChange<SavedLauncherTicket>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO LauncherTicket(Hash,SessionId,Expires) VALUES({row.Hash},{row.SessionId},{row.Expires})
                    ON CONFLICT(Hash) DO UPDATE SET SessionId=excluded.SessionId,Expires=excluded.Expires
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM LauncherTicket WHERE Hash={change.Key.Name}");
            }
        }
    }
}
