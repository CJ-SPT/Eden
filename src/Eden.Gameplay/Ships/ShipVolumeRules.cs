namespace Eden.Gameplay.Ships;

public static class ShipVolumeRules
{
    // Build 3662168 inventorycommon.const.packagedVolumeOverridesPerGroup/Type.
    // All native ship groups share these rules, including packaged market hulls.
    public static double Packaged(int typeId, HullClass hullClass, double assembledVolume) =>
        (TechOneHullType)typeId == TechOneHullType.Porpoise
            ? 50000
            : hullClass switch
            {
                HullClass.Capsule or HullClass.Shuttle or HullClass.PrototypeExploration => 500,
                HullClass.Frigate
                or HullClass.Corvette
                or HullClass.AssaultFrigate
                or HullClass.CovertOps
                or HullClass.Interceptor
                or HullClass.StealthBomber
                or HullClass.ElectronicAttack
                or HullClass.ExpeditionFrigate
                or HullClass.LogisticsFrigate => 2500,
                HullClass.MiningBarge or HullClass.Exhumer => 3750,
                HullClass.Destroyer
                or HullClass.Interdictor
                or HullClass.StrategicCruiser
                or HullClass.TacticalDestroyer
                or HullClass.CommandDestroyer => 5000,
                HullClass.Cruiser
                or HullClass.HeavyAssaultCruiser
                or HullClass.Logistics
                or HullClass.ForceRecon
                or HullClass.HeavyInterdictionCruiser
                or HullClass.CombatReconShip
                or HullClass.FlagCruiser
                or HullClass.Yacht => 10000,
                HullClass.CombatBattlecruiser or HullClass.CommandShip or HullClass.AttackBattlecruiser => 15000,
                HullClass.Hauler or HullClass.DeepSpaceTransport or HullClass.BlockadeRunner => 20000,
                HullClass.Battleship
                or HullClass.EliteBattleship
                or HullClass.BlackOps
                or HullClass.Marauder
                or HullClass.ExpeditionCommand => 50000,
                HullClass.IndustrialCommand => 500000,
                HullClass.Dreadnought
                or HullClass.Freighter
                or HullClass.Carrier
                or HullClass.Supercarrier
                or HullClass.CapitalIndustrial
                or HullClass.JumpFreighter
                or HullClass.ForceAuxiliary
                or HullClass.LancerDreadnought
                or HullClass.CommandCarrier => 1300000,
                HullClass.Titan => 10000000,
                _ => assembledVolume,
            };
}
