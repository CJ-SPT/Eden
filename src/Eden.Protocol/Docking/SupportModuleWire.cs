using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class SupportModuleWire(
    TimeProvider clock,
    IPilotFitting fitting,
    IPilotInventory inventory,
    IPilotCombat combat,
    IPilotAmmunition ammunition
)
{
    private static readonly DogmaAttributeId[] ShipAttributes =
    [
        DogmaAttributeId.MaxTargetRange,
        DogmaAttributeId.ScanResolution,
        DogmaAttributeId.GravimetricStrength,
        DogmaAttributeId.LadarStrength,
        DogmaAttributeId.MagnetometricStrength,
        DogmaAttributeId.RadarStrength,
        DogmaAttributeId.MaxVelocity,
        DogmaAttributeId.SignatureRadius,
        DogmaAttributeId.WarpScrambleStatus,
    ];
    private static readonly DogmaAttributeId[] WeaponAttributes =
    [
        DogmaAttributeId.MaxRange,
        DogmaAttributeId.AccuracyFalloff,
        DogmaAttributeId.TrackingSpeed,
    ];
    private static readonly DogmaAttributeId[] ChargeAttributes =
    [
        DogmaAttributeId.MaxVelocity,
        DogmaAttributeId.MissileFlightTime,
        DogmaAttributeId.ExplosionVelocity,
        DogmaAttributeId.ExplosionRadius,
    ];

    internal static DogmaEffect? Effect(string? name) =>
        name switch
        {
            "gunneryMaxRangeFalloffTrackingSpeedBonus" => DogmaEffect.TrackingComputer,
            "sensorBoosterActivePercentage" => DogmaEffect.SensorBooster,
            "missileGuidanceComputerBonus4" => DogmaEffect.MissileGuidanceComputer,
            _ => null,
        };

    internal static string Graphic(DogmaEffect effect) =>
        effect == DogmaEffect.SensorBooster
            ? "effects.ElectronicAttributeModifyActivate"
            : "effects.TurretWeaponRangeTrackingSpeedMultiplyActivate";

    internal void Refresh(PilotCallContext context, double? capacitor = null)
    {
        long pilot = context.Pilot.PilotId;
        long ship = context.Pilot.ShipId;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        List<ModuleAttributeChange> changes = [];
        Add(new DogmaItemKey.Item(ship), fitting.ShipAttributes(pilot), ShipAttributes);
        if (capacitor is double charge)
        {
            changes.Add(new(pilot, new DogmaItemKey.Item(ship), DogmaAttributeId.CapacitorCharge, now, charge));
        }
        foreach (var module in inventory.List(pilot, ship).Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId)))
        {
            bool turret =
                module.GroupId
                    is CombatDefinitions.EnergyWeaponGroup
                        or CombatDefinitions.HybridWeaponGroup
                        or CombatDefinitions.ProjectileWeaponGroup
                || module.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun;
            if (turret || SupportModuleDefinitions.Effect(module.GroupId) is not null || PlayerEwarRules.Groups.Contains(module.GroupId))
            {
                var values = turret ? combat.ModuleAttributes(pilot, module) : fitting.ModuleAttributes(pilot, module);
                Add(
                    new DogmaItemKey.Item(module.Id),
                    values,
                    turret ? WeaponAttributes
                        : PlayerEwarRules.Groups.Contains(module.GroupId) ? PlayerEwarRules.SourceAttributes
                        : SupportModuleDefinitions.BonusAttributes
                );
            }
        }
        foreach (var magazine in ammunition.Read(pilot, ship))
        {
            var values = ammunition.ChargeAttributes(pilot, ship, magazine.ModuleId);
            Add(MagazineWire.Key(magazine), values, ChargeAttributes);
        }
        if (changes.Count > 0)
        {
            context.Notifications.Add(new ModuleAttributesChanged(changes));
        }
        void Add(DogmaItemKey key, IReadOnlyDictionary<DogmaAttributeId, double> values, IEnumerable<DogmaAttributeId> attributes)
        {
            changes.AddRange(attributes.Where(values.ContainsKey).Select(a => new ModuleAttributeChange(pilot, key, a, now, values[a])));
        }
    }
}
