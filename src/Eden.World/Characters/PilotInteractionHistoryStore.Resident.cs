using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Characters;

internal sealed partial class PilotInteractionHistoryStore
{
    internal static CombatParty Player(WorldAction world, Pilot pilot, long shipId, int? weaponType = null, bool readSecurity = false)
    {
        int? shipType = world.Ships.Find(shipId)?.TypeId;
        return new(
            shipId,
            pilot.Id,
            pilot.CorporationId,
            null,
            shipType,
            weaponType,
            readSecurity ? world.Set<SavedSecurityStatus>().Find(pilot.Id)?.Value ?? 0 : null,
            0
        );
    }

    internal static void Damage(WorldAction world, long victimId, CombatParty attacker, double damage)
    {
        if (damage <= 0)
        {
            return;
        }
        var row =
            world.Set<SavedCombatDamage>().Find(victimId, attacker.EntityId)
            ?? world.Add(new SavedCombatDamage { VictimId = victimId, AttackerId = attacker.EntityId }).Entity;
        row.PartyJson = JsonSerializer.Serialize(attacker);
        row.Damage += damage;
    }

    internal static void Destroyed(
        WorldAction world,
        long pilotId,
        string sourceKey,
        long now,
        int systemId,
        CombatParty victim,
        CombatParty final,
        IReadOnlyList<CombatLostItem> items,
        bool shareWithAttackers = false
    )
    {
        // Include tracked entries for a fatal first shot before SaveChanges.
        var rows = world.Set<SavedCombatDamage>().Where(d => d.VictimId == victim.EntityId).ToList();
        rows.AddRange(world.Set<SavedCombatDamage>().Local.Where(d => d.VictimId == victim.EntityId && !Enumerable.Contains(rows, d)));
        var attackers = rows.Select(d =>
                (
                    JsonSerializer.Deserialize<CombatParty>(d.PartyJson) ?? throw new InvalidDataException("Invalid combat damage record.")
                ) with
                {
                    Damage = d.Damage,
                }
            )
            .ToArray();
        final = attackers.SingleOrDefault(a => a.EntityId == final.EntityId) ?? final;
        Save(
            world,
            sourceKey,
            new(
                0,
                pilotId,
                now,
                systemId,
                victim with
                {
                    Damage = attackers.Sum(a => a.Damage),
                },
                final,
                attackers.Where(a => a.EntityId != final.EntityId).ToArray(),
                items
            )
        );
        world.RemoveRange(rows);
        if (shareWithAttackers)
        {
            foreach (long participant in attackers.Select(a => a.CharacterId).Append(final.CharacterId).OfType<long>().Distinct())
            {
                if (participant != pilotId)
                {
                    Save(
                        world,
                        $"{sourceKey}:pilot:{participant}",
                        new(
                            0,
                            participant,
                            now,
                            systemId,
                            victim with
                            {
                                Damage = attackers.Sum(a => a.Damage),
                            },
                            final,
                            attackers.Where(a => a.EntityId != final.EntityId).ToArray(),
                            items
                        )
                    );
                }
            }
        }
    }

    internal static void FactionLoss(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldNpc npc,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        long now
    )
    {
        bool mining = NpcPopulationRules.IsMining(npc.Role);
        double fraction = mining
            ? Eden.Gameplay.Mining.NpcMiningRules.CorporationLossFraction
            : attributes.GetValueOrDefault(DogmaAttributeId.EntityFactionLoss);
        int faction = mining ? npc.CorporationId : npc.FactionId;
        if (faction == 0)
        {
            faction = checked((int)(catalog.Read(CatalogData.Client.Npccorporations, (long)npc.CorporationId)?.FactionID ?? 0));
        }
        if (faction == 0 || fraction == 0)
        {
            return;
        }
        var state =
            world.Set<SavedNpcStanding>().Find(pilot.Id, (long)faction)
            ?? world.Add(new SavedNpcStanding { PilotId = pilot.Id, SourceId = faction }).Entity;
        double before = state.Value;
        state.Value = StandingRules.FactionLoss(before, fraction);
        if (before != state.Value)
        {
            world.Add(
                new SavedStandingTransaction
                {
                    PilotId = pilot.Id,
                    SourceId = faction,
                    Time = now,
                    Event = StandingEvent.CombatShipKill,
                    Before = before,
                    After = state.Value,
                    VictimCorporationId = npc.CorporationId,
                    SystemId = npc.SystemId,
                    VictimTypeId = npc.TypeId,
                }
            );
        }
    }

    internal static void Save(WorldAction world, string sourceKey, ShipCombatReport report) =>
        world.Add(
            new SavedCombatReport
            {
                PilotId = report.PilotId,
                SourceKey = sourceKey,
                Time = report.Time,
                IsLoss = report.Victim.CharacterId == report.PilotId,
                Data = JsonSerializer.Serialize(report),
            }
        );
}
