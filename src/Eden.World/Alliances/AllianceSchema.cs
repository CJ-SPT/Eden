using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Gameplay.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Alliances;

internal static class AllianceSchema
{
    internal const int GovernanceAndBillingVersion = 241;

    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedAlliance>().ToTable("WorldAlliance").HasKey(row => row.Id);
        model.Entity<SavedAlliance>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedAlliance>().HasIndex(row => row.Name).IsUnique();
        model.Entity<SavedAlliance>().HasIndex(row => row.Ticker).IsUnique();
        model.Entity<SavedAllianceMember>().ToTable("WorldAllianceMember").HasKey(row => row.CorporationId);
        model.Entity<SavedAllianceMember>().Property(row => row.CorporationId).ValueGeneratedNever();
        model.Entity<SavedAllianceMember>().HasIndex(row => row.AllianceId);
        model.Entity<SavedAllianceMember>().HasOne<SavedAlliance>().WithMany().HasForeignKey(row => row.AllianceId);
        model.Entity<SavedAllianceMember>().HasOne<SavedCorporation>().WithMany().HasForeignKey(row => row.CorporationId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldAlliance(
                Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Ticker TEXT NOT NULL,
                Description TEXT NOT NULL, Url TEXT NOT NULL, CreatorId INTEGER NOT NULL,
                CreatorCorporationId INTEGER NOT NULL, ExecutorCorporationId INTEGER,
                CreatedAt INTEGER NOT NULL, ClosedAt INTEGER);
            INSERT OR IGNORE INTO WorldInventoryOwner(Id) SELECT Id FROM WorldAlliance;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldAlliance_Name ON WorldAlliance(Name);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldAlliance_Ticker ON WorldAlliance(Ticker);
            CREATE TABLE IF NOT EXISTS WorldAllianceMember(
                CorporationId INTEGER PRIMARY KEY REFERENCES WorldCorporation(Id),
                AllianceId INTEGER NOT NULL REFERENCES WorldAlliance(Id), JoinedAt INTEGER NOT NULL, ChosenExecutorId INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldAllianceMember_AllianceId ON WorldAllianceMember(AllianceId);
            """
        );

    internal static void UpgradeGovernanceAndBilling(WorldDbContext world)
    {
        var columns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldAlliance')").ToArray();
        if (!columns.Contains("LastExecutorCorporationId"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldAlliance ADD COLUMN LastExecutorCorporationId INTEGER");
        }
        if (!columns.Contains("AutomaticMaintenancePayment"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldAlliance ADD COLUMN AutomaticMaintenancePayment INTEGER");
        }
        world.Database.ExecuteSql(
            $"""
            UPDATE WorldAlliance AS alliance
            SET LastExecutorCorporationId = coalesce(alliance.ExecutorCorporationId, (
                SELECT coalesce(json_extract(change.DataJson,'$.After.ExecutorCorporationId'),
                    json_extract(change.DataJson,'$.Before.ExecutorCorporationId'))
                FROM WorldAllianceChange AS change
                WHERE change.AllianceId = alliance.Id
                    AND coalesce(json_extract(change.DataJson,'$.After.ExecutorCorporationId'),
                        json_extract(change.DataJson,'$.Before.ExecutorCorporationId')) IS NOT NULL
                ORDER BY change.Id DESC LIMIT 1))
            WHERE alliance.ClosedAt IS NULL AND alliance.LastExecutorCorporationId IS NULL;

            UPDATE WorldAlliance AS alliance
            SET AutomaticMaintenancePayment = (
                SELECT json_extract(rule.value,'$.Enabled')
                FROM WorldCorporationBillPaymentSettings AS settings, json_each(settings.DataJson,'$.Rules') AS rule
                WHERE settings.CorporationId = alliance.ExecutorCorporationId
                    AND json_extract(rule.value,'$.BillTypeId') = {AllianceBillingRules.MaintenanceBillType}
                LIMIT 1)
            WHERE alliance.ClosedAt IS NULL AND alliance.AutomaticMaintenancePayment IS NULL;

            UPDATE WorldCorporationBillPaymentSettings AS settings
            SET DataJson = json_set(settings.DataJson, '$.Rules', json((
                SELECT json_group_array(json(rule.value))
                FROM json_each(settings.DataJson,'$.Rules') AS rule
                WHERE json_extract(rule.value,'$.BillTypeId') != {AllianceBillingRules.MaintenanceBillType}
            )))
            WHERE EXISTS (
                SELECT 1 FROM json_each(settings.DataJson,'$.Rules') AS rule
                WHERE json_extract(rule.value,'$.BillTypeId') = {AllianceBillingRules.MaintenanceBillType}
            );
            """
        );
    }
}
