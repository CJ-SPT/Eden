using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Industry;

internal static class ScienceSchema
{
    internal const int Version = 129;

    internal static void UpgradeInventory(WorldDbContext world) =>
        Add(
            world,
            "WorldItem",
            [
                ("MaterialEfficiency", "INTEGER NOT NULL DEFAULT 0"),
                ("TimeEfficiency", "INTEGER NOT NULL DEFAULT 0"),
                ("BlueprintRuns", "INTEGER NOT NULL DEFAULT -1"),
            ]
        );

    internal static void UpgradeContracts(WorldDbContext world) =>
        Add(
            world,
            "WorldContractItem",
            [
                ("MaterialEfficiency", "INTEGER NOT NULL DEFAULT 0"),
                ("TimeEfficiency", "INTEGER NOT NULL DEFAULT 0"),
                ("BlueprintRuns", "INTEGER NOT NULL DEFAULT -1"),
            ]
        );

    internal static void UpgradeJobs(WorldDbContext world) =>
        Add(
            world,
            "WorldIndustryJob",
            [
                ("Activity", "INTEGER NOT NULL DEFAULT 1"),
                ("LicensedRuns", "INTEGER NOT NULL DEFAULT 0"),
                ("BlueprintCopy", "INTEGER NOT NULL DEFAULT 0"),
                ("MaterialEfficiency", "INTEGER NOT NULL DEFAULT 0"),
                ("TimeEfficiency", "INTEGER NOT NULL DEFAULT 0"),
                ("OutputRuns", "INTEGER NOT NULL DEFAULT 0"),
                ("Probability", "REAL NOT NULL DEFAULT 1"),
                ("SuccessfulRuns", "INTEGER"),
                ("OptionalTypeId", "INTEGER"),
            ]
        );

    private static void Add(WorldDbContext world, string table, IReadOnlyList<(string Name, string Definition)> columns)
    {
        var existing = world.Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table})").ToHashSet();
        foreach (var column in columns.Where(c => !existing.Contains(c.Name)))
        {
            // Table names, column names and definitions are fixed authored migration constants.
            string sql = $"ALTER TABLE {table} ADD COLUMN {column.Name} {column.Definition}";
            world.Database.ExecuteSqlRaw(sql);
        }
    }
}
