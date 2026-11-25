using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record MedalState(long Id, int CorporationId, string DataJson);

    private sealed record MedalAwardState(long Id, long MedalId, long RecipientId, string DataJson);

    private void RegisterCorporationMedals()
    {
        Register(
            new EconomyTable<SavedCorporationMedal, MedalState>(
                CorporationMedalSchema.MedalTable,
                row => new(row.Id),
                row => new(row.Id, row.CorporationId, row.DataJson),
                state =>
                    new()
                    {
                        Id = state.Id,
                        CorporationId = state.CorporationId,
                        DataJson = state.DataJson,
                    },
                ProjectCorporationMedals,
                "Id,CorporationId,DataJson",
                reader => new(reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3)),
                state => new(state.Id),
                indexes: [new(nameof(SavedCorporationMedal.CorporationId), row => row.CorporationId)],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedCorporationMedalAward, MedalAwardState>(
                CorporationMedalSchema.AwardTable,
                row => new(row.Id),
                row => new(row.Id, row.MedalId, row.RecipientId, row.DataJson),
                state =>
                    new()
                    {
                        Id = state.Id,
                        MedalId = state.MedalId,
                        RecipientId = state.RecipientId,
                        DataJson = state.DataJson,
                    },
                ProjectCorporationMedalAwards,
                "Id,MedalId,RecipientId,DataJson",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)),
                state => new(state.Id),
                indexes:
                [
                    new(nameof(SavedCorporationMedalAward.MedalId), row => row.MedalId),
                    new(nameof(SavedCorporationMedalAward.RecipientId), row => row.RecipientId),
                ],
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectCorporationMedals(WorldDbContext world, IReadOnlyList<EconomyChange<MedalState>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string rows = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<MedalState>());
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationMedal(Id,CorporationId,DataJson)
                SELECT value->>'$.Id',value->>'$.CorporationId',value->>'$.DataJson' FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET DataJson=excluded.DataJson
                """
            );
        }
    }

    private static void ProjectCorporationMedalAwards(WorldDbContext world, IReadOnlyList<EconomyChange<MedalAwardState>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            string rows = JsonSerializer.Serialize(batch.Select(change => change.Value).OfType<MedalAwardState>());
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCorporationMedalAward(Id,MedalId,RecipientId,DataJson)
                SELECT value->>'$.Id',value->>'$.MedalId',value->>'$.RecipientId',value->>'$.DataJson' FROM json_each({rows}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET DataJson=excluded.DataJson
                """
            );
        }
    }
}
