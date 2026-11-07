using Eden.Data.Corporations;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporation(
                Id INTEGER PRIMARY KEY, Name TEXT NOT NULL COLLATE NOCASE, Ticker TEXT NOT NULL COLLATE NOCASE,
                DetailsJson TEXT NOT NULL, CeoId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE RESTRICT,
                CreatorId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE RESTRICT,
                StationId INTEGER NOT NULL, SystemId INTEGER NOT NULL, CreatedAt INTEGER NOT NULL,
                MemberLimit INTEGER NOT NULL, BalanceCents INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCorporation_Name ON WorldCorporation(Name);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCorporation_Ticker ON WorldCorporation(Ticker);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCorporation_CeoId ON WorldCorporation(CeoId);
            CREATE TABLE IF NOT EXISTS WorldCorporationApplication(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                Text TEXT NOT NULL, Status INTEGER NOT NULL, AppliedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL,
                Response TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationApplication_PilotId_CorporationId
                ON WorldCorporationApplication(PilotId,CorporationId);
            CREATE TABLE IF NOT EXISTS WorldCorporationChange(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ApplicationJson TEXT, MembershipJson TEXT, AdministrationJson TEXT, RoleJson TEXT, TitleJson TEXT);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationChange_PilotId_Id ON WorldCorporationChange(PilotId,Id);
            CREATE TABLE IF NOT EXISTS WorldCorporationEmployment(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL, StartedAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationEmployment_PilotId_Id ON WorldCorporationEmployment(PilotId,Id);
            CREATE TABLE IF NOT EXISTS WorldCorporationPayment(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL, Time INTEGER NOT NULL, BalanceCents INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldCorporationMemberAccess(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL, DataJson TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldCorporationTitle(
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                TitleId INTEGER NOT NULL, DataJson TEXT NOT NULL, PRIMARY KEY(CorporationId,TitleId));
            CREATE TABLE IF NOT EXISTS WorldCorporationWallet(
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                Key INTEGER NOT NULL, BalanceCents INTEGER NOT NULL CHECK(BalanceCents>=0), PRIMARY KEY(CorporationId,Key));
            CREATE TABLE IF NOT EXISTS WorldCorporationWalletSelection(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                CorporationId INTEGER NOT NULL, Key INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldWalletLedger(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, OwnerId INTEGER NOT NULL, Key INTEGER NOT NULL, Time INTEGER NOT NULL,
                CounterpartyId INTEGER NOT NULL, AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL,
                EntryType INTEGER NOT NULL, ReferenceId INTEGER NOT NULL, Reason TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldWalletLedger_OwnerId_Key_Id ON WorldWalletLedger(OwnerId,Key,Id);
            CREATE TABLE IF NOT EXISTS WorldCorporationLoyaltyWallet(
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                IssuerCorporationId INTEGER NOT NULL, Points INTEGER NOT NULL CHECK(Points>=0),
                PRIMARY KEY(CorporationId,IssuerCorporationId));
            CREATE TABLE IF NOT EXISTS WorldCorporationLoyaltyTransfer(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, SourceOwnerId INTEGER NOT NULL, RecipientCorporationId INTEGER NOT NULL,
                IssuerCorporationId INTEGER NOT NULL, Points INTEGER NOT NULL, SourceBefore INTEGER NOT NULL,
                SourceAfter INTEGER NOT NULL, RecipientBefore INTEGER NOT NULL, RecipientAfter INTEGER NOT NULL, Time INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldCorporationBillPaymentSettings(
                CorporationId INTEGER PRIMARY KEY REFERENCES WorldCorporation(Id) ON DELETE CASCADE, DataJson TEXT NOT NULL);
            """
        );
        InventoryOwnershipSchema.Register(world);
        CorporationShareSchema.Upgrade(world);
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldNpcBounty')").Contains("TaxCents"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldNpcBounty ADD COLUMN TaxCents INTEGER NOT NULL DEFAULT 0");
        }
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCorporationChange')")
                .Contains("AdministrationJson")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCorporationChange ADD COLUMN AdministrationJson TEXT");
        }
        foreach (string column in new[] { "RoleJson", "TitleJson", "ShareJson" })
        {
            if (
                !world
                    .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCorporationChange')")
                    .Contains(column)
            )
            {
                world.Database.ExecuteSqlRaw(
                    column switch
                    {
                        "RoleJson" => "ALTER TABLE WorldCorporationChange ADD COLUMN RoleJson TEXT",
                        "TitleJson" => "ALTER TABLE WorldCorporationChange ADD COLUMN TitleJson TEXT",
                        _ => "ALTER TABLE WorldCorporationChange ADD COLUMN ShareJson TEXT",
                    }
                );
            }
        }
        if (
            world
                .Set<SavedCorporation>()
                .Any(c => !world.Pilots.Any(p => p.Id == c.CeoId && p.CorporationId == c.Id) || c.MemberLimit < 1 || c.BalanceCents < 0)
        )
        {
            throw new InvalidDataException("Saved player corporation has an invalid CEO, capacity or wallet.");
        }
    }
}
