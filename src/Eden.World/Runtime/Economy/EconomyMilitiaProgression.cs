using System.Text.Json;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyMilitiaRank(long PilotId, MilitiaFaction Faction, int HighestRank);

internal sealed record EconomyMilitiaStatistics(MilitiaStatisticOwner Scope, long OwnerId, long Kills, long VictoryPoints, string DaysJson);

internal sealed record EconomyMilitiaLoyaltyAward(
    long Id,
    long PilotId,
    int CorporationId,
    int IssuerId,
    long Points,
    long Tax,
    long Time,
    long SourceId,
    MilitiaLoyaltySource Source
);

internal sealed partial class ResidentEconomy
{
    private void RegisterMilitiaProgression()
    {
        Register(
            new EconomyTable<SavedMilitiaRank, EconomyMilitiaRank>(
                "WorldMilitiaRank",
                row => new(row.PilotId, (int)row.Faction),
                row => new(row.PilotId, row.Faction, row.HighestRank),
                row =>
                    new()
                    {
                        PilotId = row.PilotId,
                        Faction = row.Faction,
                        HighestRank = row.HighestRank,
                    },
                ProjectMilitiaRanks,
                "PilotId,Faction,HighestRank",
                r => new(r.GetInt64(1), (MilitiaFaction)r.GetInt32(2), r.GetInt32(3)),
                row => new(row.PilotId, (int)row.Faction),
                indexes: [new(nameof(SavedMilitiaRank.PilotId), row => row.PilotId)],
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedMilitiaStatistics, EconomyMilitiaStatistics>(
                "WorldMilitiaStatistics",
                row => new((int)row.Scope, row.OwnerId),
                row => new(row.Scope, row.OwnerId, row.Kills, row.VictoryPoints, row.DaysJson),
                row =>
                    new()
                    {
                        Scope = row.Scope,
                        OwnerId = row.OwnerId,
                        Kills = row.Kills,
                        VictoryPoints = row.VictoryPoints,
                        DaysJson = row.DaysJson,
                    },
                ProjectMilitiaStatistics,
                "Scope,OwnerId,Kills,VictoryPoints,DaysJson",
                r => new((MilitiaStatisticOwner)r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetString(5)),
                row => new((int)row.Scope, row.OwnerId),
                primaryColumn: null
            )
        );
        Register(
            new EconomyTable<SavedMilitiaLoyaltyAward, EconomyMilitiaLoyaltyAward>(
                "WorldMilitiaLoyaltyAward",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.CorporationId, row.IssuerId, row.Points, row.Tax, row.Time, row.SourceId, row.Source),
                row =>
                    new()
                    {
                        Id = row.Id,
                        PilotId = row.PilotId,
                        CorporationId = row.CorporationId,
                        IssuerId = row.IssuerId,
                        Points = row.Points,
                        Tax = row.Tax,
                        Time = row.Time,
                        SourceId = row.SourceId,
                        Source = row.Source,
                    },
                ProjectMilitiaAwards,
                "Id,PilotId,CorporationId,IssuerId,Points,Tax,Time,SourceId,Source",
                r =>
                    new(
                        r.GetInt64(1),
                        r.GetInt64(2),
                        r.GetInt32(3),
                        r.GetInt32(4),
                        r.GetInt64(5),
                        r.GetInt64(6),
                        r.GetInt64(7),
                        r.GetInt64(8),
                        (MilitiaLoyaltySource)r.GetInt32(9)
                    ),
                row => new(row.Id),
                indexes: [new(nameof(SavedMilitiaLoyaltyAward.PilotId), row => row.PilotId)],
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectMilitiaRanks(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaRank>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMilitiaRank WHERE (PilotId,Faction) IN
                    (SELECT json_extract(value,'$.Id'),json_extract(value,'$.Part') FROM json_each({deleted}))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<EconomyMilitiaRank>());
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaRank(PilotId,Faction,HighestRank)
                SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.Faction'),json_extract(value,'$.HighestRank')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(PilotId,Faction) DO UPDATE SET HighestRank=excluded.HighestRank
                """
            );
        }
    }

    private static void ProjectMilitiaStatistics(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaStatistics>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key));
            world.Database.ExecuteSql(
                $"""
                DELETE FROM WorldMilitiaStatistics WHERE (Scope,OwnerId) IN
                    (SELECT json_extract(value,'$.Id'),json_extract(value,'$.Part') FROM json_each({deleted}))
                """
            );
            string rows = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<EconomyMilitiaStatistics>());
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaStatistics(Scope,OwnerId,Kills,VictoryPoints,DaysJson)
                SELECT json_extract(value,'$.Scope'),json_extract(value,'$.OwnerId'),json_extract(value,'$.Kills'),
                    json_extract(value,'$.VictoryPoints'),json_extract(value,'$.DaysJson')
                FROM json_each({rows}) WHERE 1
                ON CONFLICT(Scope,OwnerId) DO UPDATE SET Kills=excluded.Kills,
                    VictoryPoints=excluded.VictoryPoints,DaysJson=excluded.DaysJson
                """
            );
        }
    }

    private static void ProjectMilitiaAwards(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyMilitiaLoyaltyAward>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string deleted = JsonSerializer.Serialize(batch.Where(change => change.Value is null).Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldMilitiaLoyaltyAward WHERE Id IN (SELECT value FROM json_each({deleted}))");
            string rows = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<EconomyMilitiaLoyaltyAward>());
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldMilitiaLoyaltyAward(Id,PilotId,CorporationId,IssuerId,Points,Tax,Time,SourceId,Source)
                SELECT json_extract(value,'$.Id'),json_extract(value,'$.PilotId'),json_extract(value,'$.CorporationId'),
                    json_extract(value,'$.IssuerId'),json_extract(value,'$.Points'),json_extract(value,'$.Tax'),
                    json_extract(value,'$.Time'),json_extract(value,'$.SourceId'),json_extract(value,'$.Source')
                FROM json_each({rows}) WHERE 1 ON CONFLICT(Id) DO NOTHING
                """
            );
        }
    }
}
