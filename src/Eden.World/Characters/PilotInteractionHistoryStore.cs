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
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed partial class PilotInteractionHistoryStore(WorldDatabase database) : IPilotInteractionHistory
{
    public IReadOnlyList<ShipCombatReport> Combat(long pilotId, int count, long? beforeId)
    {
        if (count is < 1 or > InteractionHistoryRules.MaximumCombatPageSize || beforeId < 0)
        {
            throw new InvalidDataException("Invalid combat history page.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var rows = world.Set<SavedCombatReport>().Where(r => r.PilotId == pilotId && (beforeId == null || r.Id < beforeId));
        // The native client filters this shared reply after the call. Give
        // each filter its own page, or mixed kills/losses hide its Next button.
        return rows.Where(r => !r.IsLoss)
            .OrderByDescending(r => r.Id)
            .Take(count)
            .ToArray()
            .Concat(rows.Where(r => r.IsLoss).OrderByDescending(r => r.Id).Take(count).ToArray())
            .OrderByDescending(r => r.Id)
            .Select(r => r.Snapshot())
            .ToArray();
    }

    public ShipCombatReport? Report(long pilotId, long reportId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<SavedCombatReport>().SingleOrDefault(r => r.Id == reportId && r.PilotId == pilotId)?.Snapshot();
    }

    public IReadOnlyList<NpcStanding> Standings(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<SavedNpcStanding>()
            .Where(s => s.PilotId == pilotId)
            .OrderBy(s => s.SourceId)
            .Select(s => new NpcStanding(s.SourceId, s.Value))
            .ToArray();
    }

    public IReadOnlyList<StandingTransaction> Transactions(long pilotId, long sourceId)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<SavedStandingTransaction>()
            .Where(t => t.PilotId == pilotId && t.SourceId == sourceId)
            .OrderByDescending(t => t.Id)
            .AsEnumerable()
            .Select(t => t.Snapshot())
            .ToArray();
    }

    internal static CombatParty Player(WorldDbContext world, Pilot pilot, long shipId, int? weaponType = null, bool readSecurity = false)
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

    internal static CombatParty Npc(WorldNpc npc, int? weaponType = null) =>
        new(npc.Id, null, npc.CorporationId, npc.FactionId == 0 ? null : npc.FactionId, npc.TypeId, weaponType, null, 0);

    internal static void Damage(WorldDbContext world, long victimId, CombatParty attacker, double damage)
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
        WorldDbContext world,
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
        rows.AddRange(world.Set<SavedCombatDamage>().Local.Where(d => d.VictimId == victim.EntityId && !rows.Contains(d)));
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

    internal static void Save(WorldDbContext world, string sourceKey, ShipCombatReport report) =>
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

    internal static void FactionLoss(
        WorldDbContext world,
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

    internal static IReadOnlyList<CombatLostItem> LostItems(long shipId, IReadOnlyList<OwnedItem> items, IReadOnlySet<long> survivors) =>
        items
            .Where(i => i.LocationId == shipId)
            .Select(i => new CombatLostItem(
                i.TypeId,
                i.FlagId,
                i.Singleton,
                survivors.Contains(i.Id) ? i.Quantity : 0,
                survivors.Contains(i.Id) ? 0 : i.Quantity,
                LostItems(i.Id, items, survivors)
            ))
            .ToArray();

    internal static IReadOnlyList<CombatLostItem> LostShipItems(
        long shipId,
        IReadOnlyList<OwnedItem> items,
        IReadOnlyList<StoredShipManifest> storedShips,
        IReadOnlySet<long> survivors
    ) =>
        LostItems(shipId, items, survivors)
            .Concat(
                storedShips.Select(stored => new CombatLostItem(
                    stored.TypeId,
                    stored.FlagId,
                    true,
                    survivors.Contains(stored.Id) ? 1 : 0,
                    survivors.Contains(stored.Id) ? 0 : 1,
                    LostItems(
                        stored.Id,
                        stored.Items,
                        survivors.Contains(stored.Id) ? stored.Items.Select(i => i.Id).ToHashSet() : new HashSet<long>()
                    )
                ))
            )
            .ToArray();
}
