using System.Runtime.CompilerServices;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.FactionWarfare;

[Injectable(InjectionType.Singleton)]
internal sealed class MilitiaHubStore(WorldDatabase database) : IMilitiaHubs
{
    private static readonly ConditionalWeakTable<LiveWorld, TransferSchedule> Schedules = new();
    private static readonly SpacePosition HubPosition = new(20_000_000_000, 0, -10_000_000_000);

    private sealed class TransferSchedule
    {
        internal long NextAt;
    }

    internal void Ensure(int systemId, long now)
    {
        if (!FactionWarzoneCatalog.Systems.ContainsKey(systemId) || LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        Sweep(now);
        using var owner = live.EnterPersistence("militia hub presence", TransactionParticipants.None);
        if (live.Economy.Peek<SavedMilitiaHub>(systemId) is not null)
        {
            return;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var occupier = live.Economy.Inspect(world => FactionWarzoneStore.System(world, systemId)!.Occupier);
        if (catalog.FittingType(MilitiaHubRules.TypeId(occupier)) is null)
        {
            return;
        }
        using var action = live.Economy.Begin(new(systems: [systemId]));
        var hub = action.Add(new SavedMilitiaHub { SystemId = systemId }).Entity;
        Replace(action, catalog, hub, occupier, now);
        action.Commit();
    }

    public void Sweep(long now)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        var schedule = Schedules.GetOrCreateValue(live);
        if (Volatile.Read(ref schedule.NextAt) > now)
        {
            return;
        }

        using var owner = live.EnterPersistence("militia occupation transfers", TransactionParticipants.None);
        if (schedule.NextAt > now)
        {
            return;
        }
        var pending = live.Economy.Inspect(world => world.Filter<SavedMilitiaHub>(hub => hub.TransferAt != null).ToArray());
        var due = pending.Where(hub => hub.TransferAt <= now).OrderBy(hub => hub.SystemId).ToArray();
        long next = pending.Where(hub => hub.TransferAt > now).Min(hub => hub.TransferAt) ?? long.MaxValue;
        if (due.Length > 0)
        {
            var resources = live.Economy.Inspect(world =>
                due.SelectMany(hub =>
                        MilitiaComplexStore
                            .DefenderIds(world, hub.SystemId)
                            .Concat(MilitiaObjectiveStore.ActiveNpcs(world, hub.SystemId))
                            .Prepend(hub.NpcId)
                    )
                    .Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
                    .ToArray()
            );
            using var action = live.Economy.Begin(new(systems: due.Select(hub => hub.SystemId), resources: resources));
            using CatalogDbContext catalog = new(database.CatalogPath);
            foreach (var entry in due)
            {
                var hub = action.Set<SavedMilitiaHub>().Find(entry.SystemId)!;
                var occupation = action.Set<SavedWarzoneOccupation>().Find(entry.SystemId)!;
                MilitiaFaction attacker = hub.Attacker!.Value;
                occupation.CapturedAt = hub.TransferAt;
                occupation.Occupier = attacker;
                occupation.VictoryPoints = 0;
                MilitiaAdvantageStore.Reset(action, entry.SystemId);
                MilitiaObjectiveStore.ResetForOccupation(action, catalog, entry.SystemId, now);
                MilitiaComplexStore.ResetForOccupation(action, catalog, entry.SystemId, attacker, now);
                Replace(action, catalog, hub, attacker, now);
            }
            action.Commit();
        }
        Volatile.Write(ref schedule.NextAt, next);
    }

    internal static bool CanAttack(WorldAction world, WorldNpc npc, Pilot pilot) =>
        npc.Role != NpcRole.MilitiaHub
            ? MilitiaObjectiveStore.CanAttack(world, npc, pilot)
            : world is EconomyTransaction
                && world.Set<SavedMilitiaHub>().Find(npc.SystemId) is { } hub
                && hub.NpcId == npc.Id
                && npc is { DestroyedAt: null, RetiredAt: null }
                && world.Set<SavedWarzoneOccupation>().Find(npc.SystemId) is { } occupation
                && MilitiaHubRules.CanAttack(
                    MilitiaHubRules.Phase(occupation.VictoryPoints, hub.TransferAt),
                    occupation.Occupier,
                    MilitiaEnlistmentStore.Effective(world, pilot)
                );

    internal static void RequireAttack(WorldAction world, WorldNpc npc, Pilot pilot)
    {
        if (!MilitiaObjectiveStore.CanAttack(world, npc, pilot))
        {
            throw new InvalidDataException("Only the opposing militia may attack this objective.");
        }
        if (npc.Role == NpcRole.MilitiaHub && !CanAttack(world, npc, pilot))
        {
            throw new InvalidDataException("Only the opposing militia can attack a vulnerable infrastructure hub.");
        }
    }

    internal static CombatDamageSettlement Apply(
        WorldAction world,
        WorldNpc npc,
        Pilot pilot,
        IReadOnlyDictionary<Eden.Gameplay.Dogma.DogmaAttributeId, double> attributes,
        DamageVector damage,
        long now,
        bool bypassResistances
    )
    {
        var defense = ShipHealth.DefenseFrom(attributes);
        var health = new HullDamage(
            ChargedResource.Recharged(
                npc.ShieldCharge,
                defense.Shield,
                defense.ShieldRechargeMilliseconds,
                Math.Max(0, (now - npc.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
            ),
            Math.Max(0, defense.Armor - npc.ArmorDamage),
            Math.Max(0, defense.Structure - npc.StructureDamage)
        );
        if (!CanAttack(world, npc, pilot))
        {
            // A previously paid projectile can arrive after defense restored invulnerability.
            return new(new(npc.Id, defense, health), 0, false, null);
        }
        var hit = CombatDamage.Apply(health, bypassResistances ? CombatDamage.WithoutResistances(defense) : defense, damage, npc.Id, now);
        if (hit.Applied <= 0)
        {
            return new(new(npc.Id, defense, health), 0, false, null);
        }
        var contribution =
            world.Set<SavedMilitiaHubContribution>().Find(npc.Id, pilot.Id)
            ?? world.Add(new SavedMilitiaHubContribution { NpcId = npc.Id, PilotId = pilot.Id }).Entity;
        contribution.Damage += hit.Applied;
        bool captured = MilitiaHubRules.Captures(defense, hit.Remaining);
        var remaining = captured ? hit.Remaining with { Structure = 0 } : hit.Remaining;
        npc.ShieldCharge = remaining.Shield;
        npc.ArmorDamage = defense.Armor - remaining.Armor;
        npc.StructureDamage = defense.Structure - remaining.Structure;
        npc.UpdatedAt = Math.Max(now, npc.UpdatedAt);
        CrimewatchPersistence.Npc(world, pilot.Id, npc.SystemId, now);
        if (captured)
        {
            Capture(world, npc, MilitiaEnlistmentStore.Effective(world, pilot)!.Value, now);
        }
        return new(new(npc.Id, defense, remaining), hit.Applied, captured, null);
    }

    private static void Capture(WorldAction world, WorldNpc npc, MilitiaFaction attacker, long now)
    {
        var hub = world.Set<SavedMilitiaHub>().Find(npc.SystemId)!;
        long due = MilitiaHubRules.NextTransfer(now);
        hub.TransferAt = due;
        hub.Attacker = attacker;
        npc.DestroyedAt = now;
        // Keep distinct contributors by hub identity. Read the active transaction's
        // newly added final-blow row as well as contributions from earlier actions.
        var contributors = Contributions(world, npc.Id);
        var winners = contributors
            .Select(row => world.Set<Pilot>().Find(row.PilotId))
            .OfType<Pilot>()
            .Where(pilot => MilitiaEnlistmentStore.Effective(world, pilot) == attacker)
            .OrderBy(pilot => pilot.Id)
            .ToArray();
        for (int index = 0; index < winners.Length; index++)
        {
            MilitiaLoyalty.Credit(world, attacker, winners[index], MilitiaHubRules.Share(index, winners.Length));
        }
        world.RemoveRange(contributors);
        if (LiveWorld.Find(world.WorldPath) is { } live)
        {
            world.AfterCommit(() =>
            {
                var schedule = Schedules.GetOrCreateValue(live);
                Volatile.Write(ref schedule.NextAt, Math.Min(schedule.NextAt, due));
            });
        }
    }

    internal static void Recover(WorldAction world, CatalogDbContext catalog, int system, MilitiaFaction occupier, long now)
    {
        if (world.Set<SavedMilitiaHub>().Find(system) is { TransferAt: null } hub && world.Set<WorldNpc>().Find(hub.NpcId) is { } npc)
        {
            // A fresh combat identity also invalidates paid cycles from the prior vulnerability.
            npc.RetiredAt = now;
            world.RemoveRange(Contributions(world, npc.Id));
            Replace(world, catalog, hub, occupier, now);
        }
    }

    private static SavedMilitiaHubContribution[] Contributions(WorldAction world, long npcId) =>
        world.Filter<SavedMilitiaHubContribution>(row => row.NpcId == npcId).ToArray();

    private static void Replace(WorldAction world, CatalogDbContext catalog, SavedMilitiaHub hub, MilitiaFaction occupier, long now)
    {
        int typeId = MilitiaHubRules.TypeId(occupier);
        var defense = ShipHealth.DefenseFrom(catalog.FittingAttributes(typeId));
        var npc = world
            .Add(
                new WorldNpc
                {
                    SystemId = hub.SystemId,
                    TypeId = typeId,
                    Role = NpcRole.MilitiaHub,
                    AnchorKind = NpcAnchorKind.MilitiaHub,
                    EncounterId = hub.SystemId,
                    FactionId = (int)occupier,
                    CorporationId = MilitiaRules.Corporation(occupier),
                    X = HubPosition.X,
                    Y = HubPosition.Y,
                    Z = HubPosition.Z,
                    HomeX = HubPosition.X,
                    HomeY = HubPosition.Y,
                    HomeZ = HubPosition.Z,
                    ShieldCharge = defense.Shield,
                    UpdatedAt = now,
                }
            )
            .Entity;
        world.SaveChanges();
        hub.NpcId = npc.Id;
        hub.TransferAt = null;
        hub.Attacker = null;
    }
}
