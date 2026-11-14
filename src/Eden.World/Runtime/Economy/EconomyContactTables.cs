using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedContact(long PilotId, long ContactId, double Standing, bool InWatchlist, long LabelMask);

internal sealed record EconomySavedAgentContact(long PilotId, long AgentId);

internal sealed record EconomySavedContactLabel(long PilotId, long Id, string Name, int? Color);

internal sealed partial class ResidentEconomy
{
    private void RegisterContactTables()
    {
        Register(
            new EconomyTable<SavedContact, EconomySavedContact>(
                "WorldContact",
                row => new(row.PilotId, row.ContactId),
                row => new(row.PilotId, row.ContactId, row.Standing, row.InWatchlist, row.LabelMask),
                state => new SavedContact
                {
                    PilotId = state.PilotId,
                    ContactId = state.ContactId,
                    Standing = state.Standing,
                    InWatchlist = state.InWatchlist,
                    LabelMask = state.LabelMask,
                },
                ProjectSavedContact,
                "PilotId,ContactId,Standing,InWatchlist,LabelMask",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3), reader.GetBoolean(4), reader.GetInt64(5)),
                state => new(state.PilotId, state.ContactId),
                indexes: [new("PilotId", row => row.PilotId), new("ContactId", row => row.ContactId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedAgentContact, EconomySavedAgentContact>(
                "WorldAgentContact",
                row => new(row.PilotId, row.AgentId),
                row => new(row.PilotId, row.AgentId),
                state => new SavedAgentContact { PilotId = state.PilotId, AgentId = state.AgentId },
                ProjectSavedAgentContact,
                "PilotId,AgentId",
                reader => new(reader.GetInt64(1), reader.GetInt64(2)),
                state => new(state.PilotId, state.AgentId),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedContactLabel, EconomySavedContactLabel>(
                "WorldContactLabel",
                row => new(row.PilotId, row.Id),
                row => new(row.PilotId, row.Id, row.Name, row.Color),
                state => new SavedContactLabel
                {
                    PilotId = state.PilotId,
                    Id = state.Id,
                    Name = state.Name,
                    Color = state.Color,
                },
                ProjectSavedContactLabel,
                "PilotId,Id,Name,Color",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt32(4)),
                state => new(state.PilotId, state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectSavedContact(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedContact>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldContact
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                    WorldContact.PilotId=json_extract(value,'$.Id') AND WorldContact.ContactId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldContact(PilotId,ContactId,Standing,InWatchlist,LabelMask)
                SELECT json_extract(value,'$.PilotId'),
                    json_extract(value,'$.ContactId'),json_extract(value,'$.Standing'),
                    json_extract(value,'$.InWatchlist'),json_extract(value,'$.LabelMask')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,ContactId) DO UPDATE SET
                    Standing=excluded.Standing,InWatchlist=excluded.InWatchlist,LabelMask=excluded.LabelMask
                """
            );
        }
    }

    private static void ProjectSavedAgentContact(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedAgentContact>> changes)
    {
        foreach (var batch in changes.Where(c => c.Value is null).Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldAgentContact
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                    WorldAgentContact.PilotId=json_extract(value,'$.Id') AND WorldAgentContact.AgentId=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldAgentContact(PilotId,AgentId)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.AgentId')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(PilotId,AgentId) DO NOTHING
                """
            );
        }
    }

    private static void ProjectSavedContactLabel(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedContactLabel>> changes)
    {
        // Names are unique per pilot. A sequence of renames can exchange two
        // names between checkpoints, so release the old names before writing any.
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Select(c => c.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldContactLabel
                WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                    WorldContactLabel.PilotId=json_extract(value,'$.Id') AND WorldContactLabel.Id=json_extract(value,'$.Part'))
                """
            );
        }
        foreach (var batch in changes.Where(c => c.Value is not null).Select(c => c.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldContactLabel(PilotId,Id,Name,Color)
                SELECT json_extract(value,'$.PilotId'),
                    json_extract(value,'$.Id'),json_extract(value,'$.Name'),json_extract(value,'$.Color')
                FROM json_each({values}) WHERE 1
                """
            );
        }
    }
}
