using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Drones;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class SupportModuleWire(
    TimeProvider clock,
    IPilotFitting fitting,
    IPilotInventory inventory,
    IPilotCombat combat,
    IPilotAmmunition ammunition,
    DroneAttributeUpdates droneUpdates
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
            "moduleBonusOmnidirectionalTrackingLink" => DogmaEffect.OmnidirectionalTrackingLink,
            _ => null,
        };

    internal static string? Graphic(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.CargoScan => "effects.CargoScan",
            DogmaEffect.ShipScan => "effects.ShipScan",
            DogmaEffect.TractorBeam => "effects.TractorBeam",
            DogmaEffect.Salvaging => "effects.Salvaging",
            DogmaEffect.PassiveTargeting or DogmaEffect.AutomaticTargeting => null,
            // Pinned effect 6557 declares no world graphic; the HUD cycle still uses ShipEffectChanged.
            DogmaEffect.OmnidirectionalTrackingLink => null,
            DogmaEffect.SensorBooster => "effects.ElectronicAttributeModifyActivate",
            DogmaEffect.RemoteTrackingComputer => "effects.TurretWeaponRangeTrackingSpeedMultiplyTarget",
            DogmaEffect.RemoteSensorBooster => "effects.ElectronicAttributeModifyTarget",
            _ => "effects.TurretWeaponRangeTrackingSpeedMultiplyActivate",
        };

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
        var items = inventory.List(pilot, ship);
        foreach (var module in items.Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId)))
        {
            bool turret =
                module.GroupId
                    is CombatDefinitions.EnergyWeaponGroup
                        or CombatDefinitions.HybridWeaponGroup
                        or CombatDefinitions.ProjectileWeaponGroup
                || module.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun;
            if (
                turret
                || SupportModuleDefinitions.Effect(module.GroupId) is not null
                || PlayerEwarRules.Groups.Contains(module.GroupId)
                || RemoteAssistanceRules.Effect(module.GroupId) is not null
            )
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
        changes.AddRange(droneUpdates.Read(context, includeControl: false, items));
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
