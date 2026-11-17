using Eden.Data.World.Combat;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal static class StructureCombatTargets
{
    internal static SavedStructure? Find(WorldAction world, long id) =>
        world is EconomyTransaction && world.Structure(id) is not null ? world.Set<SavedStructure>().Find(id) : null;

    internal static void Check(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long now, long? attacker = null)
    {
        if (world is not EconomyTransaction)
        {
            throw new InvalidOperationException("Structure combat requires the resident world.");
        }
        StructureMissileSettlement.Advance(world, catalog, structure.SystemId, now);
        StructureBattlePersistence.Advance(world, catalog, structure, now);
        var context = StructureBattlePersistence.Context(world, catalog, structure, now);
        var battle = StructureCombatRules.Advance(context, StructureBattlePersistence.Read(world, catalog, structure), now);
        long? ship = attacker is long pilot ? world.Pilots.Find(pilot)?.ActiveShipId : null;
        bool retaliation = ship is long id && Hostile(world, structure.Id, id, now);
        StructureCombatRules.CheckAttack(context.Structure, context.Security, battle, retaliation);
    }

    internal static bool Available(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long now) =>
        StructureCombatRules.Vulnerable(
            StructureCombatRules
                .Advance(
                    StructureBattlePersistence.Context(world, catalog, structure, now),
                    StructureBattlePersistence.Read(world, catalog, structure),
                    now
                )
                .State
        );

    internal static SpacePosition Position(SavedStructure structure) => new(structure.X, structure.Y, structure.Z);

    internal static void Aggress(WorldAction world, long pilotId, long structureId, int system, long now, long end)
    {
        Record(world, world.Pilots.Find(pilotId)!.ActiveShipId, structureId, now, end);
        var timer = CrimewatchPersistence.State(world, pilotId);
        timer.SystemId = system;
        timer.Apply(CrimewatchRules.Player(timer.Timers(), end, CrimewatchFlag.None, true, now));
    }

    internal static bool Hostile(WorldAction world, long source, long target, long now) =>
        world.Set<SavedPilotHostility>().Find(source, target)?.ExpiresAt > now;

    internal static void Record(WorldAction world, long source, long target, long now, long end)
    {
        var row =
            world.Set<SavedPilotHostility>().Find(source, target)
            ?? world.Add(new SavedPilotHostility { SourceShipId = source, TargetShipId = target }).Entity;
        row.LastAttackAt = Math.Max(row.LastAttackAt, now);
        row.ExpiresAt = Math.Max(row.ExpiresAt, checked(end + CrimewatchRules.PvpDuration.Ticks));
    }

    internal static CombatDamageSettlement Apply(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure structure,
        DamageVector damage,
        long now
    )
    {
        var defense = StructureBattlePersistence.Profile(world, catalog, structure).Defense;
        var hit = StructureBattlePersistence.Apply(world, catalog, structure, damage, now);
        bool destroyed = hit.Battle.State == StructureState.Destroyed;
        if (destroyed)
        {
            StructureDeparture.Settle(world, catalog, structure, now, destroyed: true);
        }
        return new(new(structure.Id, defense, hit.Battle.Health), hit.Applied, destroyed, null);
    }
}
