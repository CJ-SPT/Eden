using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.World.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomySavedCombatReport(long Id, string Data, bool IsLoss, long PilotId, string SourceKey, long Time);

internal sealed partial class ResidentEconomy
{
    private readonly EconomyCombatReportRows _combatReports;

    private EconomyPilot? KillboardPilot(long id) => ((EconomyTable<Pilot, EconomyPilot>)_tables[typeof(Pilot)]).Snapshot(new(id));

    private KillboardPartyKind KillboardPilotKind(long id) =>
        KillboardPilot(id)?.AccountId == CapsuleerCommunityStore.MinerAccountId ? KillboardPartyKind.Bot : KillboardPartyKind.Player;

    internal (IndexedCombatReport Report, string? Victim, string? Killer)[] Killboard(KillboardFilter filter, int count) =>
        Inspect(_ => _combatReports.Page(filter, count).Select(report => (report, Name(report.Victim), Name(report.Killer))).ToArray());

    internal IReadOnlyList<ShipCombatReport> CorporationCombatReports(long pilotId, bool losses, int count, long? beforeId) =>
        Inspect(world =>
        {
            var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
            return _combatReports.CorporationPage(pilot.CorporationId, losses, count, beforeId);
        });

    private string? Name(IndexedCombatParty party) => party.CharacterId is long id ? KillboardPilot(id)?.Name : null;

    private void RegisterCombatReports()
    {
        Register(
            new EconomyTable<SavedCombatReport, EconomySavedCombatReport>(
                "WorldCombatReport",
                row => new(row.Id),
                row => new(row.Id, row.Data, row.IsLoss, row.PilotId, row.SourceKey, row.Time),
                state => new SavedCombatReport
                {
                    Id = state.Id,
                    Data = state.Data,
                    IsLoss = state.IsLoss,
                    PilotId = state.PilotId,
                    SourceKey = state.SourceKey,
                    Time = state.Time,
                },
                ProjectSavedCombatReport,
                "Id,Data,IsLoss,PilotId,SourceKey,Time",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.GetBoolean(3),
                        reader.GetInt64(4),
                        reader.GetString(5),
                        reader.GetInt64(6)
                    ),
                state => new(state.Id),
                indexes: [new("Id", row => row.Id), new("PilotId", row => row.PilotId), new("SourceKey", row => row.SourceKey)],
                groupedRows: _combatReports
            )
        );
    }

    private static void ProjectSavedCombatReport(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySavedCombatReport>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCombatReport
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCombatReport.Id=json_extract(value,'$.Id')
                    )
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldCombatReport(
                        Id,Data,IsLoss,PilotId,SourceKey,Time
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.Data'),json_extract(value,'$.IsLoss'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.SourceKey'),json_extract(value,'$.Time')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        Data=excluded.Data,IsLoss=excluded.IsLoss,PilotId=excluded.PilotId,SourceKey=excluded.SourceKey,
                        Time=excluded.Time
                    """
                );
            }
        }
    }
}
