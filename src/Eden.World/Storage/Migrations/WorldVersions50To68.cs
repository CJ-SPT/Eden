using Eden.World.Combat.Npcs;
using Eden.World.Contracts;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class WorldVersions50To68
{
    internal static void Apply(WorldDbContext world, int version)
    {
        if (version is >= 6 and < 51)
        {
            ContractPersistence.UpgradeCourier(world);
        }
        if (version is >= 6 and < 52)
        {
            ContractPersistence.UpgradeAuctions(world);
        }
        if (version is >= 6 and < 53)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldShipDirt(
                    ShipId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    Timestamp INTEGER NOT NULL);
                """
            );
        }
        if (version is >= 6 and < 54)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldPilotSecurity(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, Value REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS WorldSecurityPending(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Due INTEGER NOT NULL, NpcId INTEGER NOT NULL, TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                    CorporationId INTEGER NOT NULL, Fraction REAL NOT NULL, Maximum REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS WorldSecurityTransaction(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Time INTEGER NOT NULL, NpcId INTEGER NOT NULL, TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                    CorporationId INTEGER NOT NULL, Modification REAL NOT NULL, NewValue REAL NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldSecurityTransaction_PilotId_Id ON WorldSecurityTransaction(PilotId,Id);
                """
            );
        }
        if (version is >= 6 and < 55)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldRedeemToken(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    AccountId INTEGER NOT NULL REFERENCES WorldAccount(Id) ON DELETE CASCADE,
                    Reference TEXT NOT NULL, TypeId INTEGER NOT NULL, Quantity INTEGER NOT NULL,
                    Label TEXT NOT NULL, Description TEXT NOT NULL, StationId INTEGER NULL,
                    CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NULL, State INTEGER NOT NULL,
                    FinishedAt INTEGER NULL, PilotId INTEGER NULL, ItemId INTEGER NULL, ReversedAt INTEGER NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldRedeemToken_AccountId_Reference ON WorldRedeemToken(AccountId,Reference);
                """
            );
        }
        if (version is >= 6 and < 56)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldFreeSkillPoints(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Points INTEGER NOT NULL, Revision INTEGER NOT NULL);
                """
            );
        }
        if (version is >= 6 and < 57)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldLoyaltyWallet(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    IssuerCorporationId INTEGER NOT NULL, Points INTEGER NOT NULL,
                    PRIMARY KEY(PilotId,IssuerCorporationId));
                CREATE TABLE IF NOT EXISTS WorldRedeemedCurrency(
                    TokenId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldRedeemToken(Id) ON DELETE CASCADE,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Kind INTEGER NOT NULL, Amount INTEGER NOT NULL, Before INTEGER NOT NULL,
                    After INTEGER NOT NULL, Time INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldRedeemedCurrency_PilotId_Time ON WorldRedeemedCurrency(PilotId,Time);
                """
            );
        }
        if (version is >= 6 and < 58)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldImplantState(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, Revision INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS WorldPilotImplant(
                    ItemId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    CloneId INTEGER NOT NULL REFERENCES WorldJumpClone(Id) ON DELETE CASCADE,
                    TypeId INTEGER NOT NULL, Slot INTEGER NOT NULL, Attribute INTEGER NOT NULL, Bonus INTEGER NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldPilotImplant_CloneId_Slot ON WorldPilotImplant(CloneId,Slot);
                """
            );
        }
        if (version is >= 6 and < 59)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldBooster(
                    ItemId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    TypeId INTEGER NOT NULL, Slot INTEGER NOT NULL, Bonus INTEGER NOT NULL,
                    StartedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldBooster_PilotId_Slot ON WorldBooster(PilotId,Slot);
                CREATE TABLE IF NOT EXISTS WorldBoosterState(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, Revision INTEGER NOT NULL);
                """
            );
            if (
                world
                    .Database.SqlQueryRaw<long>(
                        "SELECT COUNT(*) AS Value FROM pragma_table_info('WorldSkillQueue') WHERE name='PeriodsJson'"
                    )
                    .AsEnumerable()
                    .Single() == 0
            )
            {
                world.Database.ExecuteSqlRaw("ALTER TABLE WorldSkillQueue ADD COLUMN PeriodsJson TEXT NOT NULL DEFAULT '[]'");
            }
        }
        if (version is >= 6 and < 60)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldSkillInjectorState(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    NextAlphaInjection INTEGER NOT NULL, Revision INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS WorldSkillInjection(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ItemId INTEGER NOT NULL, TypeId INTEGER NOT NULL, Quantity INTEGER NOT NULL, Points INTEGER NOT NULL,
                    TotalBefore INTEGER NOT NULL, TotalAfter INTEGER NOT NULL, UsesSpent INTEGER NOT NULL, Time INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldSkillInjection_PilotId_Time ON WorldSkillInjection(PilotId,Time);
                """
            );
            if (
                world
                    .Database.SqlQueryRaw<long>(
                        "SELECT COUNT(*) AS Value FROM pragma_table_info('WorldBooster') WHERE name='RemainingInjectionUses'"
                    )
                    .AsEnumerable()
                    .Single() == 0
            )
            {
                world.Database.ExecuteSqlRaw("ALTER TABLE WorldBooster ADD COLUMN RemainingInjectionUses INTEGER NOT NULL DEFAULT 0");
            }
        }
        if (version is >= 6 and < 61)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldSkillExtraction(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ItemId INTEGER NOT NULL, Time INTEGER NOT NULL, AllocationsJson TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldSkillExtraction_PilotId_Id ON WorldSkillExtraction(PilotId,Id);
                """
            );
        }
        if (version is >= 6 and < 62)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldMail(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    SenderId INTEGER NOT NULL,
                    SentAt INTEGER NOT NULL, Subject TEXT NOT NULL, Body TEXT NOT NULL, RecipientsJson TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldMail_SenderId_SentAt ON WorldMail(SenderId,SentAt);
                CREATE TABLE IF NOT EXISTS WorldMailEntry(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    MessageId INTEGER NOT NULL REFERENCES WorldMail(Id) ON DELETE CASCADE,
                    Status INTEGER NOT NULL, Labels INTEGER NOT NULL, Deleted INTEGER NOT NULL,
                    PRIMARY KEY(PilotId,MessageId));
                CREATE INDEX IF NOT EXISTS IX_WorldMailEntry_MessageId ON WorldMailEntry(MessageId);
                CREATE TABLE IF NOT EXISTS WorldMailLabel(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Id INTEGER NOT NULL, Name TEXT NOT NULL, Color INTEGER, PRIMARY KEY(PilotId,Id));
                CREATE TABLE IF NOT EXISTS WorldMailChange(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    MessageId INTEGER NOT NULL, Origin TEXT NOT NULL, Kind INTEGER NOT NULL,
                    Status INTEGER NOT NULL, Labels INTEGER NOT NULL, LabelId INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldMailChange_PilotId_Id ON WorldMailChange(PilotId,Id);
                """
            );
        }
        if (version is >= 6 and < 63)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldMailingList(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, OwnerId INTEGER NOT NULL,
                    Key TEXT NOT NULL, Name TEXT NOT NULL, DefaultAccess INTEGER NOT NULL, DefaultRole INTEGER NOT NULL,
                    Deleted INTEGER NOT NULL, WelcomeTitle TEXT, WelcomeBody TEXT);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldMailingList_Key ON WorldMailingList(Key) WHERE Deleted = 0;
                CREATE TABLE IF NOT EXISTS WorldMailingListMember(
                    ListId INTEGER NOT NULL REFERENCES WorldMailingList(Id) ON DELETE CASCADE,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Role INTEGER NOT NULL, PRIMARY KEY(ListId,PilotId));
                CREATE INDEX IF NOT EXISTS IX_WorldMailingListMember_PilotId ON WorldMailingListMember(PilotId);
                CREATE TABLE IF NOT EXISTS WorldMailingListAccess(
                    ListId INTEGER NOT NULL REFERENCES WorldMailingList(Id) ON DELETE CASCADE,
                    EntityId INTEGER NOT NULL, Access INTEGER NOT NULL, PRIMARY KEY(ListId,EntityId));
                CREATE TABLE IF NOT EXISTS WorldMailingListChange(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ListId INTEGER NOT NULL, Origin TEXT NOT NULL, Kind INTEGER NOT NULL, Role INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldMailingListChange_PilotId_Id ON WorldMailingListChange(PilotId,Id);
                """
            );
            if (
                world
                    .Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('WorldMail') WHERE name='ListId'")
                    .AsEnumerable()
                    .Single() == 0
            )
            {
                world.Database.ExecuteSqlRaw("ALTER TABLE WorldMail ADD COLUMN ListId INTEGER");
            }
            world.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_WorldMail_ListId ON WorldMail(ListId)");
        }
        if (version is >= 6 and < 64)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldPilotNote(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    TargetId INTEGER NOT NULL, Text TEXT NOT NULL, PRIMARY KEY(PilotId,TargetId));
                """
            );
        }
        NpcPopulationSchema.Upgrade(world);
        NpcMissileSchema.Upgrade(world);
        NpcEncounterSchema.Upgrade(world);
        NpcGuardSchema.Upgrade(world);
        world.Database.ExecuteSqlRaw("PRAGMA user_version=68");
    }
}
