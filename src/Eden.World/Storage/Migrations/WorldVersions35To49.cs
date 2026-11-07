using Eden.Data.World.Propulsion;
using Eden.World.Propulsion;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class WorldVersions35To49
{
    internal static void Apply(WorldDbContext world, int version)
    {
        if (version is >= 6 and < 35)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldTackle(
                    ModuleId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    SourceShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL, TargetId INTEGER NOT NULL, TargetKind INTEGER NOT NULL,
                    Effect INTEGER NOT NULL, DurationMilliseconds REAL NOT NULL, CapacitorNeed REAL NOT NULL,
                    Range REAL NOT NULL, Strength REAL NOT NULL, WebPercent REAL NOT NULL, MwdBlock REAL NOT NULL,
                    StartedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldTackle_TargetId_TargetKind ON WorldTackle(TargetId,TargetKind);
                """
            );
        }
        if (version is >= 6 and < 36)
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldPilot ADD COLUMN SafetyLevel INTEGER NOT NULL DEFAULT 2");
        }
        if (version is >= 6 and < 37)
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldAccount ADD COLUMN CloneGrade INTEGER NOT NULL DEFAULT 0");
        }
        if (version is >= 6 and < 38)
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldShip ADD COLUMN StoredInId INTEGER NULL");
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldShip ADD COLUMN StorageFlagId INTEGER NOT NULL DEFAULT 4");
            world.Database.ExecuteSqlRaw("CREATE INDEX IX_WorldShip_OwnerId_StoredInId ON WorldShip(OwnerId,StoredInId)");
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldShipLoss ADD COLUMN StoredShipsJson TEXT NOT NULL DEFAULT '[]'");
        }
        if (version is >= 6 and < 39)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldCharacterSheet(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Biography TEXT NOT NULL, Intelligence INTEGER NOT NULL, Memory INTEGER NOT NULL,
                    Charisma INTEGER NOT NULL, Perception INTEGER NOT NULL, Willpower INTEGER NOT NULL,
                    BonusRemaps INTEGER NOT NULL, NextTimedRemap INTEGER);
                """
            );
        }
        if (version is >= 6 and < 40)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldCharacterSetting(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Key TEXT NOT NULL, Text TEXT, Binary BLOB, PRIMARY KEY(PilotId, Key),
                    CONSTRAINT CK_CharacterSetting_Value CHECK ((Text IS NULL) <> (Binary IS NULL)));
                """
            );
        }
        if (version is >= 6 and < 41)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldContact(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ContactId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Standing REAL NOT NULL, InWatchlist INTEGER NOT NULL, LabelMask INTEGER NOT NULL,
                    PRIMARY KEY(PilotId, ContactId));
                CREATE TABLE IF NOT EXISTS WorldBlockedContact(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ContactId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    PRIMARY KEY(PilotId, ContactId));
                CREATE TABLE IF NOT EXISTS WorldContactLabel(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Id INTEGER NOT NULL, Name TEXT NOT NULL, Color INTEGER, PRIMARY KEY(PilotId, Id));
                CREATE INDEX IF NOT EXISTS IX_WorldContact_ContactId ON WorldContact(ContactId);
                CREATE INDEX IF NOT EXISTS IX_WorldBlockedContact_ContactId ON WorldBlockedContact(ContactId);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldContactLabel_PilotId_Name ON WorldContactLabel(PilotId, Name);
                """
            );
        }
        if (version is >= 6 and < 42)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldBookmarkFolder(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Name TEXT NOT NULL, Description TEXT NOT NULL, IsActive INTEGER NOT NULL, IsKnown INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldBookmarkFolder_PilotId ON WorldBookmarkFolder(PilotId);
                CREATE TABLE IF NOT EXISTS WorldBookmarkSubfolder(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    FolderId INTEGER NOT NULL REFERENCES WorldBookmarkFolder(Id) ON DELETE CASCADE, Name TEXT NOT NULL,
                    CONSTRAINT AK_WorldBookmarkSubfolder_Id_FolderId UNIQUE(Id, FolderId));
                CREATE INDEX IF NOT EXISTS IX_WorldBookmarkSubfolder_FolderId ON WorldBookmarkSubfolder(FolderId);
                CREATE TABLE IF NOT EXISTS WorldBookmark(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    FolderId INTEGER NOT NULL REFERENCES WorldBookmarkFolder(Id) ON DELETE CASCADE,
                    SubfolderId INTEGER, CreatorId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ItemId INTEGER, TypeId INTEGER NOT NULL, LocationId INTEGER NOT NULL,
                    X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL,
                    Name TEXT NOT NULL, Note TEXT NOT NULL, CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER,
                    FOREIGN KEY(SubfolderId, FolderId) REFERENCES WorldBookmarkSubfolder(Id, FolderId) ON DELETE RESTRICT);
                CREATE INDEX IF NOT EXISTS IX_WorldBookmark_FolderId ON WorldBookmark(FolderId);
                CREATE INDEX IF NOT EXISTS IX_WorldBookmark_CreatorId ON WorldBookmark(CreatorId);
                CREATE INDEX IF NOT EXISTS IX_WorldBookmark_SubfolderId_FolderId ON WorldBookmark(SubfolderId, FolderId);
                """
            );
        }
        if (version is >= 6 and < 43)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldInsurancePolicy(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ShipId INTEGER NOT NULL, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    TypeId INTEGER NOT NULL, Level INTEGER NOT NULL, ValueCents INTEGER NOT NULL, PremiumCents INTEGER NOT NULL,
                    StartTime INTEGER NOT NULL, EndTime INTEGER NOT NULL, Status INTEGER NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldInsurancePolicy_ShipId ON WorldInsurancePolicy(ShipId) WHERE Status=0;
                CREATE INDEX IF NOT EXISTS IX_WorldInsurancePolicy_PilotId ON WorldInsurancePolicy(PilotId);
                CREATE TABLE IF NOT EXISTS WorldInsuranceLedger(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ShipId INTEGER NOT NULL, TypeId INTEGER NOT NULL,
                    PolicyId INTEGER REFERENCES WorldInsurancePolicy(Id) ON DELETE RESTRICT,
                    IsPayout INTEGER NOT NULL, AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldInsuranceLedger_ShipId ON WorldInsuranceLedger(ShipId) WHERE IsPayout=1;
                CREATE INDEX IF NOT EXISTS IX_WorldInsuranceLedger_PilotId ON WorldInsuranceLedger(PilotId);
                CREATE INDEX IF NOT EXISTS IX_WorldInsuranceLedger_PolicyId ON WorldInsuranceLedger(PolicyId);
                """
            );
        }
        if (version is >= 6 and < 44)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldTaskCounter(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Name TEXT NOT NULL, Count INTEGER NOT NULL, PRIMARY KEY(PilotId, Name));
                CREATE TABLE IF NOT EXISTS WorldCompletedTask(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Id INTEGER NOT NULL, Time INTEGER NOT NULL, PRIMARY KEY(PilotId, Id));
                """
            );
        }
        if (version is >= 6 and < 45)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldHomeStation(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, StationId INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS WorldHomeStationPayment(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    StationId INTEGER NOT NULL, OperatorId INTEGER NOT NULL, AmountCents INTEGER NOT NULL,
                    BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldHomeStationPayment_PilotId ON WorldHomeStationPayment(PilotId);
                CREATE TABLE IF NOT EXISTS WorldHomeCloneTransfer(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    OldShipId INTEGER NOT NULL, NewShipId INTEGER NOT NULL,
                    SourceStationId INTEGER NOT NULL, DestinationStationId INTEGER NOT NULL, Time INTEGER NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldHomeCloneTransfer_OldShipId ON WorldHomeCloneTransfer(OldShipId);
                CREATE INDEX IF NOT EXISTS IX_WorldHomeCloneTransfer_PilotId ON WorldHomeCloneTransfer(PilotId);
                """
            );
        }
        if (version is >= 6 and < 46)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldAgentContact(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    AgentId INTEGER NOT NULL, PRIMARY KEY(PilotId, AgentId));
                """
            );
        }
        if (version is >= 6 and < 47)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldJumpClone(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Active INTEGER NOT NULL, StationId INTEGER, Name TEXT NOT NULL,
                    CONSTRAINT CK_JumpClone_Location CHECK (
                        (Active = 1 AND StationId IS NULL) OR (Active = 0 AND StationId IS NOT NULL)));
                CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldJumpClone_PilotId ON WorldJumpClone(PilotId) WHERE Active = 1;
                CREATE TABLE IF NOT EXISTS WorldJumpCloneTimer(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, Time INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS WorldJumpClonePayment(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    StationId INTEGER NOT NULL, OperatorId INTEGER NOT NULL, Kind INTEGER NOT NULL,
                    AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL,
                    CloneId INTEGER NOT NULL, DestinationStationId INTEGER, OldShipId INTEGER, NewShipId INTEGER);
                CREATE INDEX IF NOT EXISTS IX_WorldJumpClonePayment_PilotId ON WorldJumpClonePayment(PilotId);
                """
            );
        }
        if (version is >= 6 and < 48)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldCalendarEvent(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Time INTEGER NOT NULL, DurationMinutes INTEGER, Title TEXT NOT NULL, Description TEXT NOT NULL,
                    Important INTEGER NOT NULL, Deleted INTEGER NOT NULL, Modified INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldCalendarEvent_OwnerId ON WorldCalendarEvent(OwnerId);
                CREATE INDEX IF NOT EXISTS IX_WorldCalendarEvent_Time ON WorldCalendarEvent(Time);
                CREATE TABLE IF NOT EXISTS WorldCalendarParticipant(
                    EventId INTEGER NOT NULL REFERENCES WorldCalendarEvent(Id) ON DELETE CASCADE,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Response INTEGER NOT NULL, PRIMARY KEY(EventId,PilotId));
                CREATE INDEX IF NOT EXISTS IX_WorldCalendarParticipant_PilotId ON WorldCalendarParticipant(PilotId);
                CREATE TABLE IF NOT EXISTS WorldCalendarChange(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    EventId INTEGER NOT NULL REFERENCES WorldCalendarEvent(Id) ON DELETE CASCADE,
                    Kind INTEGER NOT NULL, OwnerId INTEGER NOT NULL, Time INTEGER NOT NULL, OldTime INTEGER NOT NULL,
                    DurationMinutes INTEGER, Title TEXT NOT NULL, Important INTEGER NOT NULL,
                    Deleted INTEGER NOT NULL, Modified INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldCalendarChange_EventId ON WorldCalendarChange(EventId);
                CREATE INDEX IF NOT EXISTS IX_WorldCalendarChange_PilotId_Id ON WorldCalendarChange(PilotId,Id);
                """
            );
        }
        if (version is >= 6 and < 49)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldInboxNotification(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Type INTEGER NOT NULL, SenderId INTEGER NOT NULL,
                    ReceiverId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Processed INTEGER NOT NULL, Deleted INTEGER NOT NULL, Created INTEGER NOT NULL,
                    ContactLevel REAL NOT NULL, Message TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_WorldInboxNotification_ReceiverId_Id ON WorldInboxNotification(ReceiverId,Id);
                """
            );
        }
    }
}
