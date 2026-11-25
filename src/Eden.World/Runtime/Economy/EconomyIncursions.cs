using System.Text.Json;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Exploration;
using Eden.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record Campaign(long TaleId, int Clears, bool HeadquartersCleared, long? DefeatedAt, long LastUpdated);

    private sealed record Contribution(long SiteId, long PilotId, long TaleId, double Damage, double Repair, long PendingLoyalty);

    private sealed record RepairDamage(long SiteId, long ShipId, long TaleId, int SystemId, double Shield, double Armor, double Hull);

    private void RegisterIncursions()
    {
        Register(
            new EconomyTable<SavedIncursionCampaign, Campaign>(
                IncursionSchema.CampaignTable,
                row => new(row.TaleId),
                row => new(row.TaleId, row.Clears, row.HeadquartersCleared, row.DefeatedAt, row.LastUpdated),
                row => new SavedIncursionCampaign
                {
                    TaleId = row.TaleId,
                    Clears = row.Clears,
                    HeadquartersCleared = row.HeadquartersCleared,
                    DefeatedAt = row.DefeatedAt,
                    LastUpdated = row.LastUpdated,
                },
                ProjectCampaigns,
                "TaleId,Clears,HeadquartersCleared,DefeatedAt,LastUpdated",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetBoolean(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.GetInt64(5)
                    ),
                row => new(row.TaleId),
                primaryColumn: "TaleId"
            )
        );
        Register(
            new EconomyTable<SavedIncursionContribution, Contribution>(
                IncursionSchema.ContributionTable,
                row => new(row.SiteId, row.PilotId),
                row => new(row.SiteId, row.PilotId, row.TaleId, row.Damage, row.Repair, row.PendingLoyalty),
                row => new SavedIncursionContribution
                {
                    SiteId = row.SiteId,
                    PilotId = row.PilotId,
                    TaleId = row.TaleId,
                    Damage = row.Damage,
                    Repair = row.Repair,
                    PendingLoyalty = row.PendingLoyalty,
                },
                ProjectContributions,
                "SiteId,PilotId,TaleId,Damage,Repair,PendingLoyalty",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetDouble(4),
                        reader.GetDouble(5),
                        reader.GetInt64(6)
                    ),
                row => new(row.SiteId, row.PilotId),
                indexes: [new("SiteId", row => row.SiteId), new("TaleId", row => row.TaleId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedIncursionRepairDamage, RepairDamage>(
                IncursionSchema.RepairDamageTable,
                row => new(row.SiteId, row.ShipId),
                row => new(row.SiteId, row.ShipId, row.TaleId, row.SystemId, row.Shield, row.Armor, row.Hull),
                row => new SavedIncursionRepairDamage
                {
                    SiteId = row.SiteId,
                    ShipId = row.ShipId,
                    TaleId = row.TaleId,
                    SystemId = row.SystemId,
                    Shield = row.Shield,
                    Armor = row.Armor,
                    Hull = row.Hull,
                },
                ProjectRepairDamage,
                "SiteId,ShipId,TaleId,SystemId,Shield,Armor,Hull",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt32(4),
                        reader.GetDouble(5),
                        reader.GetDouble(6),
                        reader.GetDouble(7)
                    ),
                row => new(row.SiteId, row.ShipId),
                indexes: [new("SiteId", row => row.SiteId), new("ShipId", row => row.ShipId), new("TaleId", row => row.TaleId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedIncursionLoyaltyEntry, EconomyIncursionLoyaltyEntry>(
                IncursionSchema.LoyaltyJournalTable,
                row => new(row.PilotId, row.SourceId),
                row =>
                    new(
                        row.PilotId,
                        row.SourceId,
                        new(
                            row.TaleId,
                            row.SystemId,
                            row.RewardId,
                            row.RewardMessageId,
                            row.DungeonName,
                            row.Pilots,
                            row.Time,
                            row.Event,
                            row.Points
                        )
                    ),
                row => new SavedIncursionLoyaltyEntry
                {
                    PilotId = row.PilotId,
                    SourceId = row.SourceId,
                    TaleId = row.Entry.TaleId,
                    SystemId = row.Entry.SystemId,
                    RewardId = row.Entry.RewardId,
                    RewardMessageId = row.Entry.RewardMessageId,
                    DungeonName = row.Entry.DungeonName,
                    Pilots = row.Entry.Pilots,
                    Time = row.Entry.Time,
                    Event = row.Entry.Event,
                    Points = row.Entry.Points,
                },
                ProjectLoyaltyJournal,
                "PilotId,SourceId,TaleId,SystemId,RewardId,RewardMessageId,DungeonName,Pilots,Time,Event,Points",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetInt64(2),
                        new(
                            reader.GetInt64(3),
                            reader.GetInt32(4),
                            reader.GetInt32(5),
                            reader.GetInt32(6),
                            reader.GetString(7),
                            reader.GetInt32(8),
                            reader.GetInt64(9),
                            (IncursionLoyaltyEvent)reader.GetInt32(10),
                            reader.GetInt64(11)
                        )
                    ),
                row => new(row.PilotId, row.SourceId),
                indexes: [new(nameof(SavedIncursionLoyaltyEntry.PilotId), row => row.PilotId)],
                primaryColumn: null
            )
        );
    }

    private static void ProjectLoyaltyJournal(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyIncursionLoyaltyEntry>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var deleted = batch.Where(change => change.Value is null).Select(change => change.Key).ToArray();
            if (deleted.Length > 0)
            {
                string keys = JsonSerializer.Serialize(deleted);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldIncursionLoyaltyEntry
                    WHERE (PilotId,SourceId) IN (SELECT value->>'$.Id',value->>'$.Part' FROM json_each({keys}))
                    """
                );
            }
            var entries = batch.Select(change => change.Value).OfType<EconomyIncursionLoyaltyEntry>().ToArray();
            if (entries.Length > 0)
            {
                string rows = JsonSerializer.Serialize(entries);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldIncursionLoyaltyEntry(
                        PilotId,SourceId,TaleId,SystemId,RewardId,RewardMessageId,DungeonName,Pilots,Time,Event,Points)
                    SELECT value->>'$.PilotId',value->>'$.SourceId',value->>'$.Entry.TaleId',value->>'$.Entry.SystemId',
                        value->>'$.Entry.RewardId',value->>'$.Entry.RewardMessageId',value->>'$.Entry.DungeonName',
                        value->>'$.Entry.Pilots',value->>'$.Entry.Time',value->>'$.Entry.Event',value->>'$.Entry.Points'
                    FROM json_each({rows}) WHERE 1
                    ON CONFLICT(PilotId,SourceId) DO NOTHING
                    """
                );
            }
        }
    }

    private static void ProjectCampaigns(WorldDbContext world, IReadOnlyList<EconomyChange<Campaign>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldIncursionCampaign WHERE TaleId IN (SELECT value FROM json_each({deleted}))");
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldIncursionCampaign(TaleId,Clears,HeadquartersCleared,DefeatedAt,LastUpdated)
                SELECT value->>'$.TaleId',value->>'$.Clears',value->>'$.HeadquartersCleared',value->>'$.DefeatedAt',value->>'$.LastUpdated'
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(TaleId) DO UPDATE SET Clears=excluded.Clears,HeadquartersCleared=excluded.HeadquartersCleared,
                    DefeatedAt=excluded.DefeatedAt,LastUpdated=excluded.LastUpdated
                """
            );
        }
    }

    private static void ProjectContributions(WorldDbContext world, IReadOnlyList<EconomyChange<Contribution>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldIncursionContribution
                WHERE (SiteId,PilotId) IN (SELECT value->>'$.Id',value->>'$.Part' FROM json_each({deleted}))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldIncursionContribution(SiteId,PilotId,TaleId,Damage,Repair,PendingLoyalty)
                SELECT value->>'$.SiteId',value->>'$.PilotId',value->>'$.TaleId',
                    value->>'$.Damage',value->>'$.Repair',value->>'$.PendingLoyalty'
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SiteId,PilotId) DO UPDATE SET Damage=excluded.Damage,Repair=excluded.Repair,
                    PendingLoyalty=excluded.PendingLoyalty
                """
            );
        }
    }

    private static void ProjectRepairDamage(WorldDbContext world, IReadOnlyList<EconomyChange<RepairDamage>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldIncursionRepairDamage
                WHERE (SiteId,ShipId) IN (SELECT value->>'$.Id',value->>'$.Part' FROM json_each({deleted}))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Where(change => change.Value is not null).Select(change => change.Value));
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldIncursionRepairDamage(SiteId,ShipId,TaleId,SystemId,Shield,Armor,Hull)
                SELECT value->>'$.SiteId',value->>'$.ShipId',value->>'$.TaleId',value->>'$.SystemId',
                    value->>'$.Shield',value->>'$.Armor',value->>'$.Hull'
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(SiteId,ShipId) DO UPDATE SET Shield=excluded.Shield,Armor=excluded.Armor,Hull=excluded.Hull
                """
            );
        }
    }
}

internal sealed record EconomyIncursionLoyaltyEntry(long PilotId, long SourceId, IncursionLoyaltyEntry Entry);
