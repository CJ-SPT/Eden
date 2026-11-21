using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    internal static void ProjectShip(WorldDbContext world, LiveCombatShip before, LiveCombatShip after)
    {
        foreach (var (module, ready) in after.LocalCooldowns)
        {
            if (before.LocalCooldowns.GetValueOrDefault(module) != ready)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldWeaponCooldown(ModuleId,ReadyAt,Kind) VALUES({module},{ready},{(int)ModuleCooldownKind.Cycle})
                    ON CONFLICT(ModuleId) DO UPDATE SET ReadyAt=excluded.ReadyAt,Kind=excluded.Kind
                    """
                );
            }
        }
        foreach (var repair in after.Repairs.Where(repair => !before.Repairs.Contains(repair)))
        {
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldArmorRepair(ModuleId,ShipId,StartedAt,CompletesAt,Amount,Layer)
                VALUES({repair.ModuleId},{after.ShipId},{repair.StartedAt},{repair.CompletesAt},{repair.Amount},{(int)repair.Layer})
                ON CONFLICT(ModuleId) DO UPDATE SET ShipId=excluded.ShipId,StartedAt=excluded.StartedAt,
                    CompletesAt=excluded.CompletesAt,Amount=excluded.Amount,Layer=excluded.Layer
                """
            );
        }
        if (before.NpcAggression != after.NpcAggression && after.NpcAggression is { } npcTimer)
        {
            CrimewatchPersistence.Npc(world, after.PilotId, after.SystemId, npcTimer.ActiveUntil);
        }
        if (
            before.Capacitor != after.Capacitor
            || before.CapacitorAt != after.CapacitorAt
            || after.Turrets.Any(p => p.Value.ReadyAt != before.Turrets[p.Key].ReadyAt)
        )
        {
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldShipEnergy(ShipId,Charge,UpdatedAt) VALUES({after.ShipId},{after.Capacitor},{after.CapacitorAt})
                ON CONFLICT(ShipId) DO UPDATE SET Charge=excluded.Charge,UpdatedAt=excluded.UpdatedAt
                """
            );
        }
        if (before.Health != after.Health)
        {
            var health = after.Health;
            double armor = health.ArmorDamage ?? health.Defense.Armor - health.Remaining.Armor;
            double structure = health.StructureDamage ?? health.Defense.Structure - health.Remaining.Structure;
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldShipHealth(ShipId,ShieldCharge,ArmorDamage,StructureDamage,UpdatedAt)
                VALUES({after.ShipId},{health.Remaining.Shield},{armor},{structure},{health.UpdatedAt})
                ON CONFLICT(ShipId) DO UPDATE SET ShieldCharge=excluded.ShieldCharge,ArmorDamage=excluded.ArmorDamage,
                    StructureDamage=excluded.StructureDamage,UpdatedAt=excluded.UpdatedAt
                """
            );
        }
        foreach (var pair in after.Turrets)
        {
            var turret = pair.Value;
            var previous = before.Turrets[pair.Key];
            if (previous.ReadyAt != turret.ReadyAt)
            {
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldWeaponCooldown(ModuleId,ReadyAt,Kind) VALUES({pair.Key},{turret.ReadyAt},{(int)
                        ModuleCooldownKind.Cycle})
                    ON CONFLICT(ModuleId) DO UPDATE SET ReadyAt=excluded.ReadyAt,Kind=excluded.Kind
                    """
                );
            }
            if (
                turret.ChargeItemId is { } charge
                && (previous.Quantity != turret.Quantity || previous.CrystalDamage != turret.CrystalDamage)
            )
            {
                if (turret.Quantity == 0)
                {
                    world.Database.ExecuteSql($"DELETE FROM WorldItem WHERE Id={charge} AND OwnerId={after.PilotId}");
                }
                else
                {
                    world.Database.ExecuteSql(
                        $"""
                        UPDATE WorldItem SET Quantity={turret.Quantity},CrystalDamage={turret.CrystalDamage}
                        WHERE Id={charge} AND OwnerId={after.PilotId}
                        """
                    );
                }
            }
        }
        foreach (var repair in before.Repairs.Where(r => after.Repairs.All(current => current.ModuleId != r.ModuleId)))
        {
            world.Database.ExecuteSql($"DELETE FROM WorldArmorRepair WHERE ModuleId={repair.ModuleId} AND ShipId={after.ShipId}");
        }
        ProjectContributions(world, after.ShipId, before.Contributions, after.Contributions);
    }

    private sealed record NpcProjection(
        long Id,
        double? ShieldCharge,
        double? ArmorDamage,
        double? StructureDamage,
        long? UpdatedAt,
        double X,
        double Y,
        double Z,
        double VelocityX,
        double VelocityY,
        double VelocityZ,
        NpcNavigationState Navigation,
        long MotionAt,
        long ShieldRepairAt,
        long ArmorRepairAt,
        long RepairRevision
    );

    internal static void ProjectNpcs(WorldDbContext world, IReadOnlyList<CombatNpcChange> changes)
    {
        const int rowsPerBatch = 128;
        foreach (var batch in changes.Select(NpcState).OfType<NpcProjection>().Chunk(rowsPerBatch))
        {
            string rows = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                UPDATE WorldNpc SET
                    ShieldCharge=coalesce(json_extract(saved.value,'$.ShieldCharge'),ShieldCharge),
                    ArmorDamage=coalesce(json_extract(saved.value,'$.ArmorDamage'),ArmorDamage),
                    StructureDamage=coalesce(json_extract(saved.value,'$.StructureDamage'),StructureDamage),
                    UpdatedAt=coalesce(json_extract(saved.value,'$.UpdatedAt'),UpdatedAt),
                    X=json_extract(saved.value,'$.X'),Y=json_extract(saved.value,'$.Y'),Z=json_extract(saved.value,'$.Z'),
                    VelocityX=json_extract(saved.value,'$.VelocityX'),VelocityY=json_extract(saved.value,'$.VelocityY'),
                    VelocityZ=json_extract(saved.value,'$.VelocityZ'),Navigation=json_extract(saved.value,'$.Navigation'),
                    MotionAt=json_extract(saved.value,'$.MotionAt'),ShieldRepairAt=json_extract(saved.value,'$.ShieldRepairAt'),
                    ArmorRepairAt=json_extract(saved.value,'$.ArmorRepairAt'),RepairRevision=json_extract(saved.value,'$.RepairRevision')
                FROM json_each({rows}) AS saved WHERE WorldNpc.Id=json_extract(saved.value,'$.Id')
                """
            );
        }
        foreach (var (before, after) in changes)
        {
            ProjectContributions(world, after.Id, before.Contributions, after.Contributions);
        }
    }

    private static NpcProjection? NpcState(CombatNpcChange change)
    {
        var (before, after) = change;
        bool healthChanged = before.Health != after.Health;
        if (
            !healthChanged
            && before.Position == after.Position
            && before.Velocity == after.Velocity
            && before.Navigation == after.Navigation
            && before.MotionAt == after.MotionAt
            && before.RepairState == after.RepairState
        )
        {
            return null;
        }
        var health = after.Health;
        return new(
            after.Id,
            healthChanged ? health.Remaining.Shield : null,
            healthChanged ? health.ArmorDamage ?? health.Defense.Armor - health.Remaining.Armor : null,
            healthChanged ? health.StructureDamage ?? health.Defense.Structure - health.Remaining.Structure : null,
            healthChanged ? health.UpdatedAt : null,
            after.Position.X,
            after.Position.Y,
            after.Position.Z,
            after.Velocity.X,
            after.Velocity.Y,
            after.Velocity.Z,
            after.Navigation,
            after.MotionAt,
            after.RepairState.ShieldReadyAt,
            after.RepairState.ArmorReadyAt,
            after.RepairState.Revision
        );
    }

    private static void ProjectContributions(
        WorldDbContext world,
        long victim,
        IReadOnlyDictionary<long, Eden.Gameplay.Characters.CombatParty> previous,
        IReadOnlyDictionary<long, Eden.Gameplay.Characters.CombatParty> current
    )
    {
        foreach (var party in current.Values)
        {
            if (previous.TryGetValue(party.EntityId, out var prior) && prior == party)
            {
                continue;
            }
            string data = JsonSerializer.Serialize(party with { Damage = 0 });
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldCombatDamage(VictimId,AttackerId,PartyJson,Damage) VALUES({victim},{party.EntityId},{data},{party.Damage})
                ON CONFLICT(VictimId,AttackerId) DO UPDATE SET PartyJson=excluded.PartyJson,Damage=excluded.Damage
                """
            );
        }
    }
}
