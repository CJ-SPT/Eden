using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedBoosterState(long PilotId, long Revision);

internal sealed record EconomyTestingWalletGrant(long PilotId, long AmountCents, long BalanceCents, long Time);

internal sealed record EconomySavedHomeStationPayment(
    long Id,
    long PilotId,
    int StationId,
    long OperatorId,
    long AmountCents,
    long BalanceCents,
    long Time
);

internal sealed record EconomySavedJumpClonePayment(
    long Id,
    long PilotId,
    int StationId,
    long OperatorId,
    JumpClonePaymentKind Kind,
    long AmountCents,
    long BalanceCents,
    long Time,
    long CloneId,
    int? DestinationStationId,
    long? OldShipId,
    long? NewShipId
);

internal sealed record EconomySavedJumpCloneTimer(long PilotId, long Time);

internal sealed record EconomySavedPilotNote(long PilotId, long TargetId, string Text);

internal sealed record EconomySavedShipDirt(long ShipId, long Timestamp);

internal sealed record EconomySavedSkillInjectorState(long PilotId, long NextAlphaInjection, long Revision);

internal sealed partial class ResidentEconomy
{
    private void RegisterCharacterServices()
    {
        Register(
            new EconomyTable<TestingWalletGrant, EconomyTestingWalletGrant>(
                "WorldTestingGrant",
                row => new(row.PilotId),
                row => new(row.PilotId, row.AmountCents, row.BalanceCents, row.Time),
                state => new TestingWalletGrant
                {
                    PilotId = state.PilotId,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    Time = state.Time,
                },
                ProjectTestingWalletGrant,
                "PilotId,AmountCents,BalanceCents,Time",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
                state => new(state.PilotId),
                primaryColumn: "PilotId",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedBoosterState, EconomySavedBoosterState>(
                "WorldBoosterState",
                row => new(row.PilotId),
                row => new(row.PilotId, row.Revision),
                state => new SavedBoosterState { PilotId = state.PilotId, Revision = state.Revision },
                ProjectSavedBoosterState,
                "PilotId,Revision",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.PilotId),
                primaryColumn: "PilotId",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedHomeStationPayment, EconomySavedHomeStationPayment>(
                "WorldHomeStationPayment",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.StationId, row.OperatorId, row.AmountCents, row.BalanceCents, row.Time),
                state => new SavedHomeStationPayment
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    StationId = state.StationId,
                    OperatorId = state.OperatorId,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    Time = state.Time,
                },
                ProjectSavedHomeStationPayment,
                "Id,PilotId,StationId,OperatorId,AmountCents,BalanceCents,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedJumpClonePayment, EconomySavedJumpClonePayment>(
                "WorldJumpClonePayment",
                row => new(row.Id),
                row =>
                    new(
                        row.Id,
                        row.PilotId,
                        row.StationId,
                        row.OperatorId,
                        row.Kind,
                        row.AmountCents,
                        row.BalanceCents,
                        row.Time,
                        row.CloneId,
                        row.DestinationStationId,
                        row.OldShipId,
                        row.NewShipId
                    ),
                state => new SavedJumpClonePayment
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    StationId = state.StationId,
                    OperatorId = state.OperatorId,
                    Kind = state.Kind,
                    AmountCents = state.AmountCents,
                    BalanceCents = state.BalanceCents,
                    Time = state.Time,
                    CloneId = state.CloneId,
                    DestinationStationId = state.DestinationStationId,
                    OldShipId = state.OldShipId,
                    NewShipId = state.NewShipId,
                },
                ProjectSavedJumpClonePayment,
                "Id,PilotId,StationId,OperatorId,Kind,AmountCents,BalanceCents,Time,CloneId,DestinationStationId,OldShipId,NewShipId",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3),
                        reader.GetInt64(4),
                        (JumpClonePaymentKind)reader.GetInt32(5),
                        reader.GetInt64(6),
                        reader.GetInt64(7),
                        reader.GetInt64(8),
                        reader.GetInt64(9),
                        reader.IsDBNull(10) ? null : reader.GetInt32(10),
                        reader.IsDBNull(11) ? null : reader.GetInt64(11),
                        reader.IsDBNull(12) ? null : reader.GetInt64(12)
                    ),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: "Id",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedJumpCloneTimer, EconomySavedJumpCloneTimer>(
                "WorldJumpCloneTimer",
                row => new(row.PilotId),
                row => new(row.PilotId, row.Time),
                state => new SavedJumpCloneTimer { PilotId = state.PilotId, Time = state.Time },
                ProjectSavedJumpCloneTimer,
                "PilotId,Time",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.PilotId),
                primaryColumn: "PilotId",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedPilotNote, EconomySavedPilotNote>(
                "WorldPilotNote",
                row => new(row.PilotId, row.TargetId),
                row => new(row.PilotId, row.TargetId, row.Text),
                state => new SavedPilotNote
                {
                    PilotId = state.PilotId,
                    TargetId = state.TargetId,
                    Text = state.Text,
                },
                ProjectSavedPilotNote,
                "PilotId,TargetId,Text",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)),
                state => new(state.PilotId, state.TargetId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedShipDirt, EconomySavedShipDirt>(
                "WorldShipDirt",
                row => new(row.ShipId),
                row => new(row.ShipId, row.Timestamp),
                state => new SavedShipDirt { ShipId = state.ShipId, Timestamp = state.Timestamp },
                ProjectSavedShipDirt,
                "ShipId,Timestamp",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.ShipId),
                primaryColumn: "ShipId",
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedSkillInjectorState, EconomySavedSkillInjectorState>(
                "WorldSkillInjectorState",
                row => new(row.PilotId),
                row => new(row.PilotId, row.NextAlphaInjection, row.Revision),
                state => new SavedSkillInjectorState
                {
                    PilotId = state.PilotId,
                    NextAlphaInjection = state.NextAlphaInjection,
                    Revision = state.Revision,
                },
                ProjectSavedSkillInjectorState,
                "PilotId,NextAlphaInjection,Revision",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)),
                state => new(state.PilotId),
                primaryColumn: "PilotId",
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectTestingWalletGrant(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyTestingWalletGrant>> changes)
    {
        foreach (var change in changes)
        {
            if (change.Value is { } row)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldTestingGrant(PilotId,AmountCents,BalanceCents,Time)
                    VALUES({row.PilotId},{row.AmountCents},{row.BalanceCents},{row.Time})
                    """
                );
            }
            else
            {
                world.Database.ExecuteSql($"DELETE FROM WorldTestingGrant WHERE PilotId={change.Key.Id}");
            }
        }
    }

    private static void ProjectSavedBoosterState(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedBoosterState>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldBoosterState WHERE PilotId IN (SELECT value FROM json_each({keys}))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldBoosterState(PilotId,Revision)
                SELECT json_extract(value,'$.PilotId'), json_extract(value,'$.Revision')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET
                    Revision=excluded.Revision
                """
            );
        }
    }

    private static void ProjectSavedHomeStationPayment(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedHomeStationPayment>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldHomeStationPayment WHERE Id IN (SELECT value FROM json_each({keys}))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldHomeStationPayment(Id,PilotId,StationId,OperatorId,AmountCents,BalanceCents,Time)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'), json_extract(value,'$.StationId'),
                json_extract(value,'$.OperatorId'), json_extract(value,'$.AmountCents'),
                json_extract(value,'$.BalanceCents'), json_extract(value,'$.Time')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    PilotId=excluded.PilotId, StationId=excluded.StationId, OperatorId=excluded.OperatorId,
                    AmountCents=excluded.AmountCents, BalanceCents=excluded.BalanceCents, Time=excluded.Time
                """
            );
        }
    }

    private static void ProjectSavedJumpClonePayment(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedJumpClonePayment>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldJumpClonePayment WHERE Id IN (SELECT value FROM json_each({keys}))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldJumpClonePayment(
                    Id, PilotId, StationId, OperatorId, Kind, AmountCents, BalanceCents, Time, CloneId, DestinationStationId,
                    OldShipId, NewShipId)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'), json_extract(value,'$.StationId'),
                json_extract(value,'$.OperatorId'), json_extract(value,'$.Kind'), json_extract(value,'$.AmountCents'),
                json_extract(value,'$.BalanceCents'), json_extract(value,'$.Time'), json_extract(value,'$.CloneId'),
                json_extract(value,'$.DestinationStationId'), json_extract(value,'$.OldShipId'),
                json_extract(value,'$.NewShipId')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET
                    PilotId=excluded.PilotId, StationId=excluded.StationId, OperatorId=excluded.OperatorId,
                    Kind=excluded.Kind, AmountCents=excluded.AmountCents, BalanceCents=excluded.BalanceCents,
                    Time=excluded.Time, CloneId=excluded.CloneId, DestinationStationId=excluded.DestinationStationId,
                    OldShipId=excluded.OldShipId, NewShipId=excluded.NewShipId
                """
            );
        }
    }

    private static void ProjectSavedJumpCloneTimer(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedJumpCloneTimer>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldJumpCloneTimer WHERE PilotId IN (SELECT value FROM json_each({keys}))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldJumpCloneTimer(PilotId,Time)
                SELECT json_extract(value,'$.PilotId'), json_extract(value,'$.Time')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET
                    Time=excluded.Time
                """
            );
        }
    }

    private static void ProjectSavedPilotNote(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedPilotNote>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldPilotNote WHERE EXISTS (
                    SELECT 1 FROM json_each({keys})
                    WHERE PilotId=json_extract(value,'$.Id') AND TargetId=json_extract(value,'$.Part')
                )
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilotNote(PilotId,TargetId,Text)
                SELECT json_extract(value,'$.PilotId'), json_extract(value,'$.TargetId'), json_extract(value,'$.Text')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,TargetId) DO UPDATE SET
                    Text=excluded.Text
                """
            );
        }
    }

    private static void ProjectSavedShipDirt(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedShipDirt>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldShipDirt WHERE ShipId IN (SELECT value FROM json_each({keys}))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldShipDirt(ShipId,Timestamp)
                SELECT json_extract(value,'$.ShipId'), json_extract(value,'$.Timestamp')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(ShipId) DO UPDATE SET
                    Timestamp=excluded.Timestamp
                """
            );
        }
    }

    private static void ProjectSavedSkillInjectorState(
        WorldDbContext world,
        IReadOnlyList<EconomyChange<EconomySavedSkillInjectorState>> changes
    )
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldSkillInjectorState WHERE PilotId IN (SELECT value FROM json_each({keys}))
                """
            );
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSkillInjectorState(PilotId,NextAlphaInjection,Revision)
                SELECT json_extract(value,'$.PilotId'), json_extract(value,'$.NextAlphaInjection'),
                json_extract(value,'$.Revision')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId) DO UPDATE SET
                    NextAlphaInjection=excluded.NextAlphaInjection, Revision=excluded.Revision
                """
            );
        }
    }
}
