using Eden.Data.World.Skills;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

internal sealed class SkillEditorDbContext(string worldPath) : DbContext
{
    internal static string FilePath(string worldPath) => Path.GetFullPath(worldPath) + ".skills.sqlite";

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = FilePath(worldPath), Pooling = false }.ToString());

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SavedSkillEditorState>().ToTable("SkillEditorState").HasKey(row => row.Id);
        model.Entity<SavedSkillEdit>().ToTable("SkillEdit").HasKey(row => row.TypeId);
        model.Entity<SavedSkillEditRevision>().ToTable("SkillEditRevision").HasKey(row => row.Id);
        model.Entity<SavedSkillEditRevision>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedSkillEditRevision>().HasIndex(row => new { row.TypeId, row.Id });
    }
}
