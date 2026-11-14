using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedHomeCloneTransfer(
    long Id,
    long PilotId,
    long OldShipId,
    long NewShipId,
    long SourceStationId,
    int DestinationStationId,
    long Time
);

internal sealed record EconomySavedImplantState(long PilotId, long Revision);

internal sealed record EconomySavedJumpClone(long Id, long PilotId, bool Active, int? StationId, string Name);

internal sealed record EconomySavedPilotImplant(long ItemId, long CloneId, int TypeId, int Slot, DogmaAttributeId Attribute, int Bonus);

internal sealed record EconomyQueuedLevel(
    long PilotId,
    int Position,
    int TypeId,
    int Level,
    long StartPoints,
    long EndPoints,
    long? StartTime,
    long? EndTime,
    string PeriodsJson
);

internal sealed record EconomyTrainingLog(long Id, long PilotId, long Time, int TypeId, long Points, int EventId);

internal sealed record EconomySavedBooster(
    long ItemId,
    long PilotId,
    int TypeId,
    int Slot,
    int Bonus,
    int RemainingInjectionUses,
    long StartedAt,
    long ExpiresAt
);

internal sealed record EconomySavedCharacterSheet(
    long PilotId,
    string Biography,
    int Intelligence,
    int Memory,
    int Charisma,
    int Perception,
    int Willpower,
    int BonusRemaps,
    long? NextTimedRemap
);

internal sealed partial class ResidentEconomy
{
    private void RegisterCloneTrainingTables()
    {
        Register(
            new EconomyTable<SavedHomeCloneTransfer, EconomySavedHomeCloneTransfer>(
                "WorldHomeCloneTransfer",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.OldShipId, row.NewShipId, row.SourceStationId, row.DestinationStationId, row.Time),
                state => new SavedHomeCloneTransfer
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    OldShipId = state.OldShipId,
                    NewShipId = state.NewShipId,
                    SourceStationId = state.SourceStationId,
                    DestinationStationId = state.DestinationStationId,
                    Time = state.Time,
                },
                ProjectSavedHomeCloneTransfer,
                "Id,PilotId,OldShipId,NewShipId,SourceStationId,DestinationStationId,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7)
                    ),
                state => new(state.Id),
                readOnly: false,
                indexes: [new("PilotId", row => row.PilotId), new("OldShipId", row => row.OldShipId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedImplantState, EconomySavedImplantState>(
                "WorldImplantState",
                row => new(row.PilotId),
                row => new(row.PilotId, row.Revision),
                state => new SavedImplantState { PilotId = state.PilotId, Revision = state.Revision },
                ProjectSavedImplantState,
                "PilotId,Revision",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.PilotId),
                readOnly: false,
                indexes: [],
                primaryColumn: "PilotId"
            )
        );
        Register(
            new EconomyTable<SavedJumpClone, EconomySavedJumpClone>(
                "WorldJumpClone",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.Active, row.StationId, row.Name),
                state => new SavedJumpClone
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    Active = state.Active,
                    StationId = state.StationId,
                    Name = state.Name,
                },
                ProjectSavedJumpClone,
                "Id,PilotId,Active,StationId,Name",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetBoolean(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.GetString(5)
                    ),
                state => new(state.Id),
                readOnly: false,
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedPilotImplant, EconomySavedPilotImplant>(
                "WorldPilotImplant",
                row => new(row.ItemId),
                row => new(row.ItemId, row.CloneId, row.TypeId, row.Slot, row.Attribute, row.Bonus),
                state => new SavedPilotImplant
                {
                    ItemId = state.ItemId,
                    CloneId = state.CloneId,
                    TypeId = state.TypeId,
                    Slot = state.Slot,
                    Attribute = state.Attribute,
                    Bonus = state.Bonus,
                },
                ProjectSavedPilotImplant,
                "ItemId,CloneId,TypeId,Slot,Attribute,Bonus",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        (DogmaAttributeId)reader.GetInt32(5),
                        reader.GetInt32(6)
                    ),
                state => new(state.ItemId),
                readOnly: false,
                indexes: [new("CloneId", row => row.CloneId)],
                primaryColumn: "ItemId"
            )
        );
        Register(
            new EconomyTable<QueuedLevel, EconomyQueuedLevel>(
                "WorldSkillQueue",
                row => new(row.PilotId, row.Position),
                row =>
                    new(
                        row.PilotId,
                        row.Position,
                        row.TypeId,
                        row.Level,
                        row.StartPoints,
                        row.EndPoints,
                        row.StartTime,
                        row.EndTime,
                        row.PeriodsJson
                    ),
                state => new QueuedLevel
                {
                    PilotId = state.PilotId,
                    Position = state.Position,
                    TypeId = state.TypeId,
                    Level = state.Level,
                    StartPoints = state.StartPoints,
                    EndPoints = state.EndPoints,
                    StartTime = state.StartTime,
                    EndTime = state.EndTime,
                    PeriodsJson = state.PeriodsJson,
                },
                ProjectQueuedLevel,
                "PilotId,Position,TypeId,Level,StartPoints,EndPoints,StartTime,EndTime,PeriodsJson",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetInt64(8),
                        reader.GetString(9)
                    ),
                state => new(state.PilotId, state.Position),
                readOnly: false,
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<TrainingLog, EconomyTrainingLog>(
                "WorldSkillHistory",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.Time, row.TypeId, row.Points, row.EventId),
                state => new TrainingLog
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    Time = state.Time,
                    TypeId = state.TypeId,
                    Points = state.Points,
                    EventId = state.EventId,
                },
                ProjectTrainingLog,
                "Id,PilotId,Time,TypeId,Points,EventId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetInt64(5),
                        reader.GetInt32(6)
                    ),
                state => new(state.Id),
                readOnly: false,
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "Id"
            )
        );
        Register(
            new EconomyTable<SavedBooster, EconomySavedBooster>(
                "WorldBooster",
                row => new(row.ItemId),
                row =>
                    new(row.ItemId, row.PilotId, row.TypeId, row.Slot, row.Bonus, row.RemainingInjectionUses, row.StartedAt, row.ExpiresAt),
                state => new SavedBooster
                {
                    ItemId = state.ItemId,
                    PilotId = state.PilotId,
                    TypeId = state.TypeId,
                    Slot = state.Slot,
                    Bonus = state.Bonus,
                    RemainingInjectionUses = state.RemainingInjectionUses,
                    StartedAt = state.StartedAt,
                    ExpiresAt = state.ExpiresAt,
                },
                ProjectSavedBooster,
                "ItemId,PilotId,TypeId,Slot,Bonus,RemainingInjectionUses,StartedAt,ExpiresAt",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8)
                    ),
                state => new(state.ItemId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "ItemId"
            )
        );
        Register(
            new EconomyTable<SavedCharacterSheet, EconomySavedCharacterSheet>(
                "WorldCharacterSheet",
                row => new(row.PilotId),
                row =>
                    new(
                        row.PilotId,
                        row.Biography,
                        row.Intelligence,
                        row.Memory,
                        row.Charisma,
                        row.Perception,
                        row.Willpower,
                        row.BonusRemaps,
                        row.NextTimedRemap
                    ),
                state => new SavedCharacterSheet
                {
                    PilotId = state.PilotId,
                    Biography = state.Biography,
                    Intelligence = state.Intelligence,
                    Memory = state.Memory,
                    Charisma = state.Charisma,
                    Perception = state.Perception,
                    Willpower = state.Willpower,
                    BonusRemaps = state.BonusRemaps,
                    NextTimedRemap = state.NextTimedRemap,
                },
                ProjectSavedCharacterSheet,
                "PilotId,Biography,Intelligence,Memory,Charisma,Perception,Willpower,BonusRemaps,NextTimedRemap",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8),
                        reader.IsDBNull(9) ? null : reader.GetInt64(9)
                    ),
                state => new(state.PilotId),
                indexes: [],
                primaryColumn: "PilotId",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectSavedCharacterSheet(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCharacterSheet>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            long[] removed = batch.Where(change => change.Value is null).Select(change => change.Key.Id).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql($"DELETE FROM WorldCharacterSheet WHERE PilotId IN (SELECT value FROM json_each({deleted}))");
            }
            var rows = batch.Where(change => change.Value is not null).Select(change => change.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldCharacterSheet(
                        PilotId,Biography,Intelligence,Memory,Charisma,Perception,Willpower,BonusRemaps,NextTimedRemap
                    )
                    SELECT
                        json_extract(value,'$.PilotId'),json_extract(value,'$.Biography'),
                        json_extract(value,'$.Intelligence'),json_extract(value,'$.Memory'),json_extract(value,'$.Charisma'),
                        json_extract(value,'$.Perception'),json_extract(value,'$.Willpower'),
                        json_extract(value,'$.BonusRemaps'),json_extract(value,'$.NextTimedRemap')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId) DO UPDATE SET
                        Biography=excluded.Biography,Intelligence=excluded.Intelligence,Memory=excluded.Memory,
                        Charisma=excluded.Charisma,Perception=excluded.Perception,Willpower=excluded.Willpower,
                        BonusRemaps=excluded.BonusRemaps,NextTimedRemap=excluded.NextTimedRemap
                    """
                );
            }
        }
    }

    private static void ProjectSavedHomeCloneTransfer(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedHomeCloneTransfer>> changes
    )
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldHomeCloneTransfer(Id,PilotId,OldShipId,NewShipId,SourceStationId,DestinationStationId,Time)
                    VALUES({row.Id},{row.PilotId},{row.OldShipId},{row.NewShipId},
                        {row.SourceStationId},{row.DestinationStationId},{row.Time})
                    ON CONFLICT(Id) DO UPDATE SET
                        PilotId=excluded.PilotId,
                        OldShipId=excluded.OldShipId,
                        NewShipId=excluded.NewShipId,
                        SourceStationId=excluded.SourceStationId,
                        DestinationStationId=excluded.DestinationStationId,
                        Time=excluded.Time
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM WorldHomeCloneTransfer WHERE Id={change.Key.Id}");
            }
        }
    }

    private static void ProjectSavedImplantState(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedImplantState>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldImplantState(PilotId,Revision)
                    VALUES({row.PilotId},{row.Revision})
                    ON CONFLICT(PilotId) DO UPDATE SET
                        Revision=excluded.Revision
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM WorldImplantState WHERE PilotId={change.Key.Id}");
            }
        }
    }

    private static void ProjectSavedJumpClone(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedJumpClone>> changes)
    {
        // Release the previous active body before writing the new one, regardless of key order.
        foreach (var change in changes.OrderBy(change => change.Value?.Active == true))
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldJumpClone(Id,PilotId,Active,StationId,Name)
                    VALUES({row.Id},{row.PilotId},{row.Active},{row.StationId},{row.Name})
                    ON CONFLICT(Id) DO UPDATE SET
                        PilotId=excluded.PilotId,
                        Active=excluded.Active,
                        StationId=excluded.StationId,
                        Name=excluded.Name
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM WorldJumpClone WHERE Id={change.Key.Id}");
            }
        }
    }

    private static void ProjectSavedBooster(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedBooster>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldBooster WHERE ItemId IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldBooster(ItemId,PilotId,TypeId,Slot,Bonus,RemainingInjectionUses,StartedAt,ExpiresAt)
                SELECT json_extract(value,'$.ItemId'), json_extract(value,'$.PilotId'), json_extract(value,'$.TypeId'),
                    json_extract(value,'$.Slot'), json_extract(value,'$.Bonus'), json_extract(value,'$.RemainingInjectionUses'),
                    json_extract(value,'$.StartedAt'), json_extract(value,'$.ExpiresAt')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(ItemId) DO UPDATE SET
                    TypeId=excluded.TypeId, Slot=excluded.Slot, Bonus=excluded.Bonus, StartedAt=excluded.StartedAt,
                    ExpiresAt=excluded.ExpiresAt, RemainingInjectionUses=excluded.RemainingInjectionUses
                """
            );
        }
    }

    private static void ProjectSavedPilotImplant(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedPilotImplant>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldPilotImplant(ItemId,CloneId,TypeId,Slot,Attribute,Bonus)
                    VALUES({row.ItemId},{row.CloneId},{row.TypeId},{row.Slot},{(int)row.Attribute},{row.Bonus})
                    ON CONFLICT(ItemId) DO UPDATE SET
                        CloneId=excluded.CloneId,
                        TypeId=excluded.TypeId,
                        Slot=excluded.Slot,
                        Attribute=excluded.Attribute,
                        Bonus=excluded.Bonus
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM WorldPilotImplant WHERE ItemId={change.Key.Id}");
            }
        }
    }

    private static void ProjectQueuedLevel(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyQueuedLevel>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldSkillQueue(PilotId,Position,TypeId,Level,StartPoints,EndPoints,StartTime,EndTime,PeriodsJson)
                    VALUES({row.PilotId},{row.Position},{row.TypeId},{row.Level},{row.StartPoints},
                        {row.EndPoints},{row.StartTime},{row.EndTime},{row.PeriodsJson})
                    ON CONFLICT(PilotId,Position) DO UPDATE SET
                        TypeId=excluded.TypeId,
                        Level=excluded.Level,
                        StartPoints=excluded.StartPoints,
                        EndPoints=excluded.EndPoints,
                        StartTime=excluded.StartTime,
                        EndTime=excluded.EndTime,
                        PeriodsJson=excluded.PeriodsJson
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM WorldSkillQueue WHERE PilotId={change.Key.Id} AND Position={change.Key.Part}");
            }
        }
    }

    private static void ProjectTrainingLog(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyTrainingLog>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldSkillHistory(Id,PilotId,Time,TypeId,Points,EventId)
                    VALUES({row.Id},{row.PilotId},{row.Time},{row.TypeId},{row.Points},{row.EventId})
                    ON CONFLICT(Id) DO UPDATE SET
                        PilotId=excluded.PilotId,
                        Time=excluded.Time,
                        TypeId=excluded.TypeId,
                        Points=excluded.Points,
                        EventId=excluded.EventId
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM WorldSkillHistory WHERE Id={change.Key.Id}");
            }
        }
    }
}
