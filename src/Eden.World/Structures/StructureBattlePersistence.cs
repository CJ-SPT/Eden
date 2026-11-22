using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal static class StructureBattlePersistence
{
    private const ulong JitterIdentityMultiplier = 11400714819323198485UL;

    // Build 3662168 dogma attributes, verified against structures.structure and the pinned type catalog.
    private const int ShieldDamageLimit = 2034;
    private const int ArmorDamageLimit = 2035;
    private const int HullDamageLimit = 2036;
    private const int ShieldPauseThreshold = 3354;
    private const int ArmorPauseThreshold = 3355;
    private const int HullPauseThreshold = 3356;
    private const int CanHaveAutoRepair = 5770;
    private static readonly CatalogDefinition<StructureCombatProfile> s_profiles = new("structure-combat", Load);

    internal static StructureCombatProfile Profile(CatalogDbContext catalog, int typeId) => catalog.Definition(s_profiles, typeId);

    internal static StructureCombatProfile Profile(WorldAction world, CatalogDbContext catalog, SavedStructure structure)
    {
        var basis = Profile(catalog, structure.TypeId);
        return world is EconomyTransaction
            ? basis with
            {
                Defense = ShipHealth.DefenseFrom(StructureEquipmentPersistence.Read(world, catalog, structure).Attributes),
            }
            : basis;
    }

    private static StructureCombatProfile Load(CatalogDbContext catalog, int typeId)
    {
        var attributes = catalog.FittingAttributes(typeId);
        double Value(int id, double fallback = 0) => attributes.GetValueOrDefault((DogmaAttributeId)id, fallback);
        return new(
            ShipHealth.DefenseFrom(attributes),
            new(Value(ShieldDamageLimit), Value(ArmorDamageLimit), Value(HullDamageLimit)),
            new(Value(ShieldPauseThreshold), Value(ArmorPauseThreshold), Value(HullPauseThreshold)),
            Value(CanHaveAutoRepair, 1) != 0
        );
    }

    internal static StructureBattle Read(WorldAction world, CatalogDbContext catalog, SavedStructure structure)
    {
        var saved = world.Set<SavedStructureBattle>().Find(structure.Id);
        var defense = Profile(world, catalog, structure).Defense;
        var health =
            saved is not null ? new HullDamage(saved.Shield, saved.Armor, saved.Hull)
            : structure.State is StructureState.Onlining or StructureState.AnchorVulnerable or StructureState.DeployVulnerable
                ? new(0, 0, defense.Structure)
            : defense.Undamaged;
        if (saved is not null && world is EconomyTransaction)
        {
            // Legacy rows held the unmodified catalog capacities. New rows retain their exact saved capacity image.
            var legacy = Profile(catalog, structure.TypeId).Defense;
            double Resize(double current, double previous, double basis, double capacity) =>
                Math.Clamp(
                    (
                        previous > 0 ? current / previous
                        : basis > 0 ? current / basis
                        : 1
                    ) * capacity,
                    0,
                    capacity
                );
            health = new(
                Resize(saved.Shield, saved.ShieldCapacity, legacy.Shield, defense.Shield),
                Resize(saved.Armor, saved.ArmorCapacity, legacy.Armor, defense.Armor),
                Resize(saved.Hull, saved.HullCapacity, legacy.Structure, defense.Structure)
            );
        }
        return new(
            structure.State,
            structure.StateStartedAt,
            structure.StateEndsAt,
            health,
            saved?.RepairAt ?? (structure.State == StructureState.Onlining && structure.HasCore ? structure.StateEndsAt : null),
            saved?.DamageWindowAt ?? 0,
            new(saved?.ShieldWindow ?? 0, saved?.ArmorWindow ?? 0, saved?.HullWindow ?? 0),
            saved?.PausedUntil ?? 0
        );
    }

    internal static StructureBattleContext Context(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long now)
    {
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        long jitterLimit = profile.Size switch
        {
            StructureSize.Medium => StructureCombatRules.MediumExitJitter,
            StructureSize.Flex => StructureCombatRules.FlexExitJitter,
            _ => StructureCombatRules.LargeExitJitter,
        };
        // Stable identity/time mixing gives a reproducible exit offset; its saved deadline is authoritative thereafter.
        ulong seed = unchecked((ulong)structure.Id * JitterIdentityMultiplier ^ (ulong)now);
        long jitter = checked((long)(seed % (ulong)(2 * jitterLimit + 1))) - jitterLimit;
        return new(
            profile,
            Profile(world, catalog, structure),
            world.Filter<SavedStructureService>(m => m.StructureId == structure.Id && m.Online).Any(),
            StructureRules.AbandonmentDeadline(structure.TypeId, structure.LowPowerAt, structure.AbandonAt) <= now,
            WormholeRules.IsWormholeSystem(structure.SystemId),
            catalog.Definitions.SystemSecurity.GetValueOrDefault(structure.SystemId),
            structure.ReinforceHour,
            jitter
        )
        {
            HasCore = structure.HasCore,
        };
    }

    internal static StructureHit Apply(WorldAction world, CatalogDbContext catalog, SavedStructure structure, DamageVector damage, long now)
    {
        if (world is not EconomyTransaction)
        {
            throw new InvalidOperationException("Structure battles require the resident world.");
        }
        var before = Read(world, catalog, structure);
        var hit = StructureCombatRules.Apply(Context(world, catalog, structure, now), before, damage, now);
        if (hit.Battle.State == StructureState.Onlining && structure.HasCore)
        {
            hit = hit with { Battle = hit.Battle with { EndsAt = hit.Battle.RepairAt } };
        }
        Write(world, catalog, structure, hit.Battle);
        if (
            before.State != hit.Battle.State
            && hit.Battle.State is StructureState.HullReinforced or StructureState.HullVulnerable or StructureState.Destroyed
        )
        {
            foreach (var module in world.Filter<SavedStructureService>(m => m.StructureId == structure.Id && m.Online))
            {
                module.Online = false;
                module.NextFuelAt = null;
                world.Set<Eden.Data.World.Inventory.OwnedItem>().Find(module.ItemId)!.Online = false;
            }
            structure.LowPowerAt ??= now;
            world.SaveChanges();
            StructureIndustry.Synchronize(world, catalog, structure, now);
        }
        if (hit.Applied > 0 && hit.Battle.State != StructureState.ShieldVulnerable)
        {
            structure.UnanchorAt = null;
        }
        return hit;
    }

    internal static void Advance(WorldAction world, CatalogDbContext catalog, SavedStructure structure, long now)
    {
        if (world.Set<SavedStructureBattle>().Find(structure.Id) is null)
        {
            return;
        }
        var before = Read(world, catalog, structure);
        var after = StructureCombatRules.Advance(Context(world, catalog, structure, now), before, now);
        if (before != after)
        {
            Write(world, catalog, structure, after);
        }
    }

    internal static void Restore(WorldAction world, CatalogDbContext catalog, SavedStructure structure)
    {
        if (world.Set<SavedStructureBattle>().Find(structure.Id) is not null)
        {
            Write(
                world,
                catalog,
                structure,
                new(
                    structure.State,
                    structure.StateStartedAt,
                    structure.StateEndsAt,
                    Profile(world, catalog, structure).Defense.Undamaged,
                    null,
                    0,
                    new(0, 0, 0),
                    0
                )
            );
        }
    }

    internal static void Write(WorldAction world, CatalogDbContext catalog, SavedStructure structure, StructureBattle battle)
    {
        var row = world.Set<SavedStructureBattle>().Find(structure.Id);
        if (row is null)
        {
            row = new() { StructureId = structure.Id };
            world.Add(row);
        }
        structure.State = battle.State;
        structure.StateStartedAt = battle.StartedAt;
        structure.StateEndsAt = battle.EndsAt;
        row.Shield = battle.Health.Shield;
        row.Armor = battle.Health.Armor;
        row.Hull = battle.Health.Structure;
        var defense = Profile(world, catalog, structure).Defense;
        row.ShieldCapacity = defense.Shield;
        row.ArmorCapacity = defense.Armor;
        row.HullCapacity = defense.Structure;
        row.RepairAt = battle.RepairAt;
        row.DamageWindowAt = battle.DamageWindowAt;
        row.ShieldWindow = battle.WindowDamage.Shield;
        row.ArmorWindow = battle.WindowDamage.Armor;
        row.HullWindow = battle.WindowDamage.Structure;
        row.PausedUntil = battle.PausedUntil;
        if (battle.State != StructureState.DeployVulnerable)
        {
            structure.DeployStartedAt = null;
            structure.DeployEndsAt = null;
        }
    }
}
