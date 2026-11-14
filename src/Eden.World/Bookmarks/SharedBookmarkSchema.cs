using Eden.Data.World.AccessGroups;
using Eden.Data.World.Bookmarks;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Gameplay.AccessGroups;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Bookmarks;

internal static class SharedBookmarkSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedAccessGroup>().ToTable("WorldAccessGroup").HasKey(g => g.Id);
        // CreatorId is a pilot for authored lists and a corporation for built-in lists.
        model.Entity<SavedAccessGroup>().HasIndex(g => new { g.CreatorId, g.IsCorporation });
        model.Entity<SavedAccessGroupLog>().ToTable("WorldAccessGroupLog").HasKey(l => l.Id);
        model.Entity<SavedAccessGroupLog>().HasOne<SavedAccessGroup>().WithMany().HasForeignKey(l => l.GroupId);
        model.Entity<SavedAccessGroupLog>().HasIndex(l => new { l.GroupId, l.Id });
        model.Entity<SavedAccessGroupMember>().ToTable("WorldAccessGroupMember").HasKey(m => new { m.GroupId, m.OwnerId });
        model.Entity<SavedAccessGroupMember>().HasIndex(m => new { m.OwnerId, m.Role });
        model.Entity<SavedAccessGroupMember>().HasOne<SavedAccessGroup>().WithMany().HasForeignKey(m => m.GroupId);
        model.Entity<SavedBookmarkConnection>().ToTable("WorldBookmarkConnection").HasKey(c => new { c.PilotId, c.FolderId });
        model.Entity<SavedBookmarkConnection>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        model.Entity<SavedBookmarkConnection>().HasOne<SavedBookmarkFolder>().WithMany().HasForeignKey(c => c.FolderId);
        model
            .Entity<SavedBookmarkFolder>()
            .HasOne<SavedAccessGroup>()
            .WithMany()
            .HasForeignKey(f => f.AdminGroupId)
            .OnDelete(DeleteBehavior.SetNull);
        model
            .Entity<SavedBookmarkFolder>()
            .HasOne<SavedAccessGroup>()
            .WithMany()
            .HasForeignKey(f => f.ManageGroupId)
            .OnDelete(DeleteBehavior.SetNull);
        model
            .Entity<SavedBookmarkFolder>()
            .HasOne<SavedAccessGroup>()
            .WithMany()
            .HasForeignKey(f => f.UseGroupId)
            .OnDelete(DeleteBehavior.SetNull);
        model
            .Entity<SavedBookmarkFolder>()
            .HasOne<SavedAccessGroup>()
            .WithMany()
            .HasForeignKey(f => f.ViewGroupId)
            .OnDelete(DeleteBehavior.SetNull);
    }

    internal static void Upgrade(WorldDbContext world, CatalogDbContext catalog)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldAccessGroup(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatorId INTEGER NOT NULL, IsCorporation INTEGER NOT NULL,
                Name TEXT NOT NULL, Description TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldAccessGroup_CreatorId_IsCorporation ON WorldAccessGroup(CreatorId,IsCorporation);
            CREATE TABLE IF NOT EXISTS WorldAccessGroupLog(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, GroupId INTEGER NOT NULL REFERENCES WorldAccessGroup(Id) ON DELETE CASCADE,
                ActorId INTEGER NOT NULL, Time INTEGER NOT NULL, Change INTEGER NOT NULL, OwnerId INTEGER,
                OldRole INTEGER, NewRole INTEGER, OldText TEXT, NewText TEXT);
            CREATE INDEX IF NOT EXISTS IX_WorldAccessGroupLog_GroupId_Id ON WorldAccessGroupLog(GroupId,Id);
            CREATE TABLE IF NOT EXISTS WorldAccessGroupMember(
                GroupId INTEGER NOT NULL REFERENCES WorldAccessGroup(Id) ON DELETE CASCADE,
                OwnerId INTEGER NOT NULL, Role INTEGER NOT NULL CHECK(Role BETWEEN 0 AND 3), PRIMARY KEY(GroupId,OwnerId));
            CREATE INDEX IF NOT EXISTS IX_WorldAccessGroupMember_OwnerId_Role ON WorldAccessGroupMember(OwnerId,Role);
            CREATE TABLE IF NOT EXISTS WorldBookmarkConnection(
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                FolderId INTEGER NOT NULL REFERENCES WorldBookmarkFolder(Id) ON DELETE CASCADE,
                IsActive INTEGER NOT NULL, PRIMARY KEY(PilotId,FolderId));
            CREATE INDEX IF NOT EXISTS IX_WorldBookmarkConnection_FolderId ON WorldBookmarkConnection(FolderId);
            """
        );
        var columns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldBookmarkFolder')").ToArray();
        if (!columns.Contains("IsPersonal"))
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldBookmarkFolder ADD COLUMN IsPersonal INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE WorldBookmarkFolder ADD COLUMN AdminGroupId INTEGER REFERENCES WorldAccessGroup(Id) ON DELETE SET NULL;
                ALTER TABLE WorldBookmarkFolder ADD COLUMN ManageGroupId INTEGER REFERENCES WorldAccessGroup(Id) ON DELETE SET NULL;
                ALTER TABLE WorldBookmarkFolder ADD COLUMN UseGroupId INTEGER REFERENCES WorldAccessGroup(Id) ON DELETE SET NULL;
                ALTER TABLE WorldBookmarkFolder ADD COLUMN ViewGroupId INTEGER REFERENCES WorldAccessGroup(Id) ON DELETE SET NULL;
                """
            );
        }
        world.Database.ExecuteSqlRaw(
            """
            CREATE INDEX IF NOT EXISTS IX_WorldBookmarkFolder_AdminGroupId ON WorldBookmarkFolder(AdminGroupId);
            CREATE INDEX IF NOT EXISTS IX_WorldBookmarkFolder_ManageGroupId ON WorldBookmarkFolder(ManageGroupId);
            CREATE INDEX IF NOT EXISTS IX_WorldBookmarkFolder_UseGroupId ON WorldBookmarkFolder(UseGroupId);
            CREATE INDEX IF NOT EXISTS IX_WorldBookmarkFolder_ViewGroupId ON WorldBookmarkFolder(ViewGroupId);
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldBookmarkSubfolder')")
                .Contains("CreatorId")
        )
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldBookmarkSubfolder ADD COLUMN CreatorId INTEGER NOT NULL DEFAULT 0;
                UPDATE WorldBookmarkSubfolder SET CreatorId=(SELECT PilotId FROM WorldBookmarkFolder WHERE Id=FolderId);
                """
            );
        }
        var corporations = catalog
            .Definitions.NpcCorporations.Concat(
                world.Set<SavedCorporation>().Select(c => new KeyValuePair<int, string>(c.Id, c.Name)).ToArray()
            )
            .ToArray();
        EnsureCorporations(world, corporations);
    }

    internal static void EnsureCorporation(WorldAction world, int corporationId, string name)
    {
        if (world.Filter<SavedAccessGroup>(g => g.IsCorporation && g.CreatorId == corporationId).Any())
        {
            return;
        }
        var group = world
            .Add(
                new SavedAccessGroup
                {
                    CreatorId = corporationId,
                    IsCorporation = true,
                    Name = name,
                }
            )
            .Entity;
        world.SaveChanges();
        world.Add(
            new SavedAccessGroupMember
            {
                GroupId = group.Id,
                OwnerId = corporationId,
                Role = AccessGroupRole.Admin,
            }
        );
    }

    private static void EnsureCorporations(WorldDbContext world, IReadOnlyList<KeyValuePair<int, string>> corporations)
    {
        var existing = world.Set<SavedAccessGroup>().Where(g => g.IsCorporation).Select(g => g.CreatorId).ToHashSet();
        var added = corporations
            .Where(c => !existing.Contains(c.Key))
            .DistinctBy(c => c.Key)
            .Select(c => new SavedAccessGroup
            {
                CreatorId = c.Key,
                IsCorporation = true,
                Name = c.Value,
            })
            .ToArray();
        if (added.Length == 0)
        {
            return;
        }
        world.AddRange(added);
        world.SaveChanges();
        world.AddRange(
            added.Select(g => new SavedAccessGroupMember
            {
                GroupId = g.Id,
                OwnerId = g.CreatorId,
                Role = AccessGroupRole.Admin,
            })
        );
        world.SaveChanges();
    }
}
