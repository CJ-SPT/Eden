using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Mail;
using Eden.Gameplay.Mail;
using Eden.Gameplay.Notifications;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedInboxNotification(
    long Id,
    InboxNotificationType Type,
    long SenderId,
    long ReceiverId,
    bool Processed,
    bool Deleted,
    long Created,
    double ContactLevel,
    string Message
);

internal sealed record EconomySavedMail(
    long Id,
    long SenderId,
    long SentAt,
    long? ListId,
    string Subject,
    string Body,
    string RecipientsJson
);

internal sealed record EconomySavedMailChange(
    long Id,
    long PilotId,
    long MessageId,
    Guid Origin,
    MailChangeKind Kind,
    MailStatus Status,
    long Labels,
    long LabelId
);

internal sealed record EconomySavedMailEntry(long PilotId, long MessageId, MailStatus Status, long Labels, bool Deleted);

internal sealed record EconomySavedMailLabel(long PilotId, long Id, string Name, int? Color);

internal sealed record EconomySavedMailingList(
    long Id,
    long OwnerId,
    string Key,
    string Name,
    MailingListAccess DefaultAccess,
    MailingListRole DefaultRole,
    bool Deleted,
    string? WelcomeTitle,
    string? WelcomeBody
);

internal sealed record EconomySavedMailingListAccess(long ListId, long EntityId, MailingListAccess Access);

internal sealed record EconomySavedMailingListChange(
    long Id,
    long PilotId,
    long ListId,
    Guid Origin,
    MailingListChangeKind Kind,
    MailingListRole Role
);

internal sealed record EconomySavedMailingListMember(long ListId, long PilotId, MailingListRole Role);

internal sealed record EconomySavedBlockedContact(long PilotId, long ContactId);

internal sealed partial class ResidentEconomy
{
    private readonly Dictionary<long, long> _mailHeads = [];
    private readonly Dictionary<long, long> _listHeads = [];

    private void MailChangePublished(EconomySavedMailChange? before, EconomySavedMailChange? after) =>
        UpdateFeedHead<SavedMailChange, EconomySavedMailChange>(_mailHeads, before?.PilotId, before?.Id, after?.PilotId, after?.Id);

    private void MailingListChangePublished(EconomySavedMailingListChange? before, EconomySavedMailingListChange? after) =>
        UpdateFeedHead<SavedMailingListChange, EconomySavedMailingListChange>(
            _listHeads,
            before?.PilotId,
            before?.Id,
            after?.PilotId,
            after?.Id
        );

    private void UpdateFeedHead<TRow, TRowState>(Dictionary<long, long> heads, long? oldPilot, long? oldId, long? newPilot, long? newId)
        where TRow : class
        where TRowState : class
    {
        if (oldPilot is long previous && oldPilot != newPilot && heads.GetValueOrDefault(previous) == oldId)
        {
            var table = (EconomyTable<TRow, TRowState>)_tables[typeof(TRow)];
            long remaining = table.Indexed("PilotId", previous).Select(key => key.Id).DefaultIfEmpty().Max();
            if (remaining == 0)
            {
                heads.Remove(previous);
            }
            else
            {
                heads[previous] = remaining;
            }
        }
        if (newPilot is long pilot && newId is long id && id > heads.GetValueOrDefault(pilot))
        {
            heads[pilot] = id;
        }
    }

    internal long MailHead(long pilot) => _mailHeads.GetValueOrDefault(pilot);

    internal long MailingListHead(long pilot) => _listHeads.GetValueOrDefault(pilot);

    private void RegisterMailTables()
    {
        Register(
            new EconomyTable<SavedInboxNotification, EconomySavedInboxNotification>(
                "WorldInboxNotification",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.Type,
                        row.SenderId,
                        row.ReceiverId,
                        row.Processed,
                        row.Deleted,
                        row.Created,
                        row.ContactLevel,
                        row.Message
                    ),
                state => new SavedInboxNotification
                {
                    Id = state.Id,
                    Type = state.Type,
                    SenderId = state.SenderId,
                    ReceiverId = state.ReceiverId,
                    Processed = state.Processed,
                    Deleted = state.Deleted,
                    Created = state.Created,
                    ContactLevel = state.ContactLevel,
                    Message = state.Message,
                },
                ProjectSavedInboxNotification,
                "Id,Type,SenderId,ReceiverId,Processed,Deleted,Created,ContactLevel," + "Message",
                reader =>
                    new(
                        reader.GetInt64(1),
                        (InboxNotificationType)reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetBoolean(5),
                        reader.GetBoolean(6),
                        reader.GetInt64(7),
                        reader.GetDouble(8),
                        reader.GetString(9)
                    ),
                state => new(state.Id),
                indexes: [new("ReceiverId", row => row.ReceiverId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedMail, EconomySavedMail>(
                "WorldMail",
                row => new(row.Id),
                row => new(row.Id, row.SenderId, row.SentAt, row.ListId, row.Subject, row.Body, row.RecipientsJson),
                state => new SavedMail
                {
                    Id = state.Id,
                    SenderId = state.SenderId,
                    SentAt = state.SentAt,
                    ListId = state.ListId,
                    Subject = state.Subject,
                    Body = state.Body,
                    RecipientsJson = state.RecipientsJson,
                },
                ProjectSavedMail,
                "Id,SenderId,SentAt,ListId,Subject,Body,RecipientsJson",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7)
                    ),
                state => new(state.Id),
                indexes: [new("SenderId", row => row.SenderId), new("ListId", row => row.ListId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedMailChange, EconomySavedMailChange>(
                "WorldMailChange",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.MessageId, row.Origin, row.Kind, row.Status, row.Labels, row.LabelId),
                state => new SavedMailChange
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    MessageId = state.MessageId,
                    Origin = state.Origin,
                    Kind = state.Kind,
                    Status = state.Status,
                    Labels = state.Labels,
                    LabelId = state.LabelId,
                },
                ProjectSavedMailChange,
                "Id,PilotId,MessageId,Origin,Kind,Status,Labels,LabelId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetGuid(4),
                        (MailChangeKind)reader.GetInt32(5),
                        (MailStatus)reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                changed: MailChangePublished,
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedMailEntry, EconomySavedMailEntry>(
                "WorldMailEntry",
                row => new(row.PilotId, row.MessageId),
                row => new(row.PilotId, row.MessageId, row.Status, row.Labels, row.Deleted),
                state => new SavedMailEntry
                {
                    PilotId = state.PilotId,
                    MessageId = state.MessageId,
                    Status = state.Status,
                    Labels = state.Labels,
                    Deleted = state.Deleted,
                },
                ProjectSavedMailEntry,
                "PilotId,MessageId,Status,Labels,Deleted",
                reader =>
                    new(reader.GetInt64(1), reader.GetInt64(2), (MailStatus)reader.GetInt32(3), reader.GetInt64(4), reader.GetBoolean(5)),
                state => new(state.PilotId, state.MessageId),
                indexes: [new("PilotId", row => row.PilotId), new("MessageId", row => row.MessageId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedMailLabel, EconomySavedMailLabel>(
                "WorldMailLabel",
                row => new(row.PilotId, row.Id),
                row => new(row.PilotId, row.Id, row.Name, row.Color),
                state => new SavedMailLabel
                {
                    PilotId = state.PilotId,
                    Id = state.Id,
                    Name = state.Name,
                    Color = state.Color,
                },
                ProjectSavedMailLabel,
                "PilotId,Id,Name,Color",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt32(4)),
                state => new(state.PilotId, state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedMailingList, EconomySavedMailingList>(
                "WorldMailingList",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.OwnerId,
                        row.Key,
                        row.Name,
                        row.DefaultAccess,
                        row.DefaultRole,
                        row.Deleted,
                        row.WelcomeTitle,
                        row.WelcomeBody
                    ),
                state => new SavedMailingList
                {
                    Id = state.Id,
                    OwnerId = state.OwnerId,
                    Key = state.Key,
                    Name = state.Name,
                    DefaultAccess = state.DefaultAccess,
                    DefaultRole = state.DefaultRole,
                    Deleted = state.Deleted,
                    WelcomeTitle = state.WelcomeTitle,
                    WelcomeBody = state.WelcomeBody,
                },
                ProjectSavedMailingList,
                "Id,OwnerId,Key,Name,DefaultAccess,DefaultRole,Deleted,WelcomeTitle," + "WelcomeBody",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        (MailingListAccess)reader.GetInt32(5),
                        (MailingListRole)reader.GetInt32(6),
                        reader.GetBoolean(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetString(9)
                    ),
                state => new(state.Id),
                indexes: [new("Key", row => row.Key), new("OwnerId", row => row.OwnerId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedMailingListAccess, EconomySavedMailingListAccess>(
                "WorldMailingListAccess",
                row => new(row.ListId, row.EntityId),
                row => new(row.ListId, row.EntityId, row.Access),
                state => new SavedMailingListAccess
                {
                    ListId = state.ListId,
                    EntityId = state.EntityId,
                    Access = state.Access,
                },
                ProjectSavedMailingListAccess,
                "ListId,EntityId,Access",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), (MailingListAccess)reader.GetInt32(3)),
                state => new(state.ListId, state.EntityId),
                indexes: [new("ListId", row => row.ListId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedMailingListChange, EconomySavedMailingListChange>(
                "WorldMailingListChange",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.ListId, row.Origin, row.Kind, row.Role),
                state => new SavedMailingListChange
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    ListId = state.ListId,
                    Origin = state.Origin,
                    Kind = state.Kind,
                    Role = state.Role,
                },
                ProjectSavedMailingListChange,
                "Id,PilotId,ListId,Origin,Kind,Role",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetGuid(4),
                        (MailingListChangeKind)reader.GetInt32(5),
                        (MailingListRole)reader.GetInt32(6)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                changed: MailingListChangePublished,
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedMailingListMember, EconomySavedMailingListMember>(
                "WorldMailingListMember",
                row => new(row.ListId, row.PilotId),
                row => new(row.ListId, row.PilotId, row.Role),
                state => new SavedMailingListMember
                {
                    ListId = state.ListId,
                    PilotId = state.PilotId,
                    Role = state.Role,
                },
                ProjectSavedMailingListMember,
                "ListId,PilotId,Role",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), (MailingListRole)reader.GetInt32(3)),
                state => new(state.ListId, state.PilotId),
                indexes: [new("ListId", row => row.ListId), new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedBlockedContact, EconomySavedBlockedContact>(
                "WorldBlockedContact",
                row => new(row.PilotId, row.ContactId),
                row => new(row.PilotId, row.ContactId),
                state => new SavedBlockedContact { PilotId = state.PilotId, ContactId = state.ContactId },
                ProjectSavedBlockedContact,
                "PilotId,ContactId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.PilotId, state.ContactId),
                indexes: [new("PilotId", row => row.PilotId), new("ContactId", row => row.ContactId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectSavedInboxNotification(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedInboxNotification>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldInboxNotification
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldInboxNotification.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldInboxNotification(
                        Id,"Type",SenderId,ReceiverId,Processed,Deleted,Created,
                        ContactLevel,Message
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.Type'),json_extract(value,'$.SenderId'),
                        json_extract(value,'$.ReceiverId'),json_extract(value,'$.Processed'),json_extract(value,'$.Deleted'),
                        json_extract(value,'$.Created'),json_extract(value,'$.ContactLevel'),json_extract(value,'$.Message')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        "Type"=excluded."Type",SenderId=excluded.SenderId,ReceiverId=excluded.ReceiverId,
                        Processed=excluded.Processed,Deleted=excluded.Deleted,Created=excluded.Created,
                        ContactLevel=excluded.ContactLevel,Message=excluded.Message
                """
            );
        }
    }

    private static void ProjectSavedMail(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMail>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMail
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldMail.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMail(
                        Id,SenderId,SentAt,ListId,Subject,Body,RecipientsJson
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.SenderId'),json_extract(value,'$.SentAt'),
                        json_extract(value,'$.ListId'),json_extract(value,'$.Subject'),json_extract(value,'$.Body'),
                        json_extract(value,'$.RecipientsJson')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        SenderId=excluded.SenderId,SentAt=excluded.SentAt,ListId=excluded.ListId,
                        Subject=excluded.Subject,Body=excluded.Body,RecipientsJson=excluded.RecipientsJson
                """
            );
        }
    }

    private static void ProjectSavedMailChange(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMailChange>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMailChange
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldMailChange.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMailChange(
                        Id,PilotId,MessageId,Origin,Kind,Status,Labels,
                        LabelId
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.MessageId'),
                        json_extract(value,'$.Origin'),json_extract(value,'$.Kind'),json_extract(value,'$.Status'),
                        json_extract(value,'$.Labels'),json_extract(value,'$.LabelId')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        PilotId=excluded.PilotId,MessageId=excluded.MessageId,Origin=excluded.Origin,
                        Kind=excluded.Kind,Status=excluded.Status,Labels=excluded.Labels,
                        LabelId=excluded.LabelId
                """
            );
        }
    }

    private static void ProjectSavedMailEntry(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMailEntry>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMailEntry
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldMailEntry.PilotId=json_extract(value,'$.Id') AND
                            WorldMailEntry.MessageId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMailEntry(
                        PilotId,MessageId,Status,Labels,Deleted
                )
                SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.MessageId'),json_extract(value,'$.Status'),
                        json_extract(value,'$.Labels'),json_extract(value,'$.Deleted')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,MessageId) DO UPDATE SET
                        Status=excluded.Status,Labels=excluded.Labels,Deleted=excluded.Deleted
                """
            );
        }
    }

    private static void ProjectSavedMailLabel(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMailLabel>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMailLabel
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldMailLabel.PilotId=json_extract(value,'$.Id') AND
                            WorldMailLabel.Id=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMailLabel(
                        PilotId,Id,Name,Color
                )
                SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.Id'),json_extract(value,'$.Name'),
                        json_extract(value,'$.Color')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,Id) DO UPDATE SET
                        Name=excluded.Name,Color=excluded.Color
                """
            );
        }
    }

    private static void ProjectSavedMailingList(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedMailingList>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMailingList
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldMailingList.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (
            var batch in changes
                .Where(c => c.Value is not null)
                .OrderByDescending(c => c.Value!.Deleted)
                .Select(c => c.Value)
                .Chunk(ProjectionBatchSize)
        )
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMailingList(
                        Id,OwnerId,"Key",Name,DefaultAccess,DefaultRole,Deleted,
                        WelcomeTitle,WelcomeBody
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.OwnerId'),json_extract(value,'$.Key'),
                        json_extract(value,'$.Name'),json_extract(value,'$.DefaultAccess'),json_extract(value,'$.DefaultRole'),
                        json_extract(value,'$.Deleted'),json_extract(value,'$.WelcomeTitle'),json_extract(value,'$.WelcomeBody')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        OwnerId=excluded.OwnerId,"Key"=excluded."Key",Name=excluded.Name,
                        DefaultAccess=excluded.DefaultAccess,DefaultRole=excluded.DefaultRole,Deleted=excluded.Deleted,
                        WelcomeTitle=excluded.WelcomeTitle,WelcomeBody=excluded.WelcomeBody
                """
            );
        }
    }

    private static void ProjectSavedMailingListAccess(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedMailingListAccess>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMailingListAccess
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldMailingListAccess.ListId=json_extract(value,'$.Id') AND
                            WorldMailingListAccess.EntityId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMailingListAccess(
                        ListId,EntityId,Access
                )
                SELECT
                        json_extract(value,'$.ListId'),json_extract(value,'$.EntityId'),json_extract(value,'$.Access')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(ListId,EntityId) DO UPDATE SET
                        Access=excluded.Access
                """
            );
        }
    }

    private static void ProjectSavedMailingListChange(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedMailingListChange>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMailingListChange
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldMailingListChange.Id=json_extract(value,'$.Id'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMailingListChange(
                        Id,PilotId,ListId,Origin,Kind,Role
                )
                SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.ListId'),
                        json_extract(value,'$.Origin'),json_extract(value,'$.Kind'),json_extract(value,'$.Role')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                        PilotId=excluded.PilotId,ListId=excluded.ListId,Origin=excluded.Origin,
                        Kind=excluded.Kind,Role=excluded.Role
                """
            );
        }
    }

    private static void ProjectSavedMailingListMember(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedMailingListMember>> changes
    )
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMailingListMember
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                            WorldMailingListMember.ListId=json_extract(value,'$.Id') AND
                            WorldMailingListMember.PilotId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMailingListMember(
                        ListId,PilotId,Role
                )
                SELECT
                        json_extract(value,'$.ListId'),json_extract(value,'$.PilotId'),json_extract(value,'$.Role')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(ListId,PilotId) DO UPDATE SET
                        Role=excluded.Role
                """
            );
        }
    }

    private static void ProjectSavedBlockedContact(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedBlockedContact>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldBlockedContact
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                    WorldBlockedContact.PilotId=json_extract(value,'$.Id') AND
                    WorldBlockedContact.ContactId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldBlockedContact(PilotId,ContactId)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.ContactId') FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,ContactId) DO NOTHING
                """
            );
        }
    }
}
