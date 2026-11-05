using System.Text.Json;
using Eden.Gameplay.Characters;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class InteractionHistorySchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCombatReport(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                SourceKey TEXT NOT NULL, Time INTEGER NOT NULL, IsLoss INTEGER NOT NULL, Data TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCombatReport_SourceKey ON WorldCombatReport(SourceKey);
            CREATE INDEX IF NOT EXISTS IX_WorldCombatReport_PilotId_IsLoss_Id ON WorldCombatReport(PilotId,IsLoss,Id);
            CREATE TABLE IF NOT EXISTS WorldCombatDamage(
                VictimId INTEGER NOT NULL, AttackerId INTEGER NOT NULL, PartyJson TEXT NOT NULL, Damage REAL NOT NULL,
                PRIMARY KEY(VictimId,AttackerId));
            CREATE TABLE IF NOT EXISTS WorldNpcStanding(
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                SourceId INTEGER NOT NULL, Value REAL NOT NULL, PRIMARY KEY(PilotId,SourceId));
            CREATE TABLE IF NOT EXISTS WorldStandingTransaction(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                SourceId INTEGER NOT NULL, Time INTEGER NOT NULL, Event INTEGER NOT NULL, Before REAL NOT NULL,
                After REAL NOT NULL, VictimCorporationId INTEGER NOT NULL, SystemId INTEGER NOT NULL, VictimTypeId INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldStandingTransaction_PilotId_SourceId_Id
                ON WorldStandingTransaction(PilotId,SourceId,Id);
            """
        );
        Backfill(world);
    }

    private static void Backfill(WorldDbContext world)
    {
        List<(string Key, ShipCombatReport Report)> reports = [];
        var pilots = world.Pilots.ToDictionary(p => p.Id);
        var npcs = world.Set<WorldNpc>().ToDictionary(n => n.Id);
        foreach (var loss in world.Set<ShipLoss>().AsNoTracking())
        {
            var pilot = pilots[loss.PilotId];
            var npc = npcs[loss.NpcId];
            var items = JsonSerializer.Deserialize<OwnedItem[]>(loss.ItemsJson) ?? [];
            var survivors = JsonSerializer.Deserialize<long[]>(loss.DroppedItemIdsJson) ?? [];
            var stored = JsonSerializer.Deserialize<StoredShipManifest[]>(loss.StoredShipsJson) ?? [];
            CombatParty victim = new(loss.ShipId, pilot.Id, pilot.CorporationId, null, loss.TypeId, null, null, 0);
            reports.Add(
                (
                    $"ship:{loss.ShipId}",
                    new(
                        0,
                        pilot.Id,
                        loss.Time,
                        loss.SystemId,
                        victim,
                        PilotInteractionHistoryStore.Npc(npc),
                        [],
                        PilotInteractionHistoryStore.LostShipItems(loss.ShipId, items, stored, survivors.ToHashSet()),
                        Historical: true
                    )
                )
            );
        }
        foreach (var bounty in world.Set<NpcBounty>().AsNoTracking())
        {
            var pilot = pilots[bounty.PilotId];
            if (!npcs.TryGetValue(bounty.NpcId, out var npc))
            {
                continue;
            }
            // The old bounty journal proves the kill, but never saved the
            // attacking hull, weapon, damage totals or NPC loot roll.
            CombatParty final = new(0, pilot.Id, pilot.CorporationId, null, null, null, null, 0);
            reports.Add(
                (
                    $"npc:{bounty.NpcId}:{bounty.Time}",
                    new(0, pilot.Id, bounty.Time, bounty.SystemId, PilotInteractionHistoryStore.Npc(npc), final, [], [], Historical: true)
                )
            );
        }
        var existing = world.Set<SavedCombatReport>().Select(r => r.SourceKey).ToHashSet();
        foreach (var (key, report) in reports.OrderBy(r => r.Report.Time).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            if (existing.Add(key))
            {
                PilotInteractionHistoryStore.Save(world, key, report);
            }
        }
        world.SaveChanges();
    }
}
