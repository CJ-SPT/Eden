using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Reprocessing;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Targeting;

namespace Eden.Gameplay.Capsuleers;

public enum CapsuleerProfession
{
    Mining = 1,
    Hauling,
    Combat,
}

public enum CapsuleerTrainingFocus
{
    Work,
    Ship,
    Foundation,
}

public sealed record CapsuleerCareerPlan(CapsuleerProfession Profession, int ShipTypeId, CapsuleerTrainingFocus NextFocus)
{
    public int HighestHullStage { get; init; }
}

public static class CapsuleerCareerTrainingRules
{
    private const int FoundationLevel = 4;
    private const int SpaceshipCommand = 3327;
    private const int EvasiveManeuvering = 3453;
    private const int TechOne = 1;

    public static int HullStage(CapsuleerProfession profession, HullClass hull, int techLevel, bool standardVariant) =>
        (profession, hull, techLevel, standardVariant) switch
        {
            (CapsuleerProfession.Mining, HullClass.Frigate, TechOne, true) => 1,
            (CapsuleerProfession.Mining, HullClass.MiningBarge, TechOne, true) => 2,
            (CapsuleerProfession.Mining, HullClass.ExpeditionFrigate or HullClass.Exhumer or HullClass.Frigate, _, _) => 3,
            (CapsuleerProfession.Mining, HullClass.IndustrialCommand or HullClass.CapitalIndustrial or HullClass.ExpeditionCommand, _, _) =>
                4,
            (CapsuleerProfession.Hauling, HullClass.Hauler, TechOne, true) => 1,
            (CapsuleerProfession.Hauling, HullClass.DeepSpaceTransport or HullClass.BlockadeRunner or HullClass.Freighter, _, _) => 2,
            (CapsuleerProfession.Hauling, HullClass.JumpFreighter, _, _) => 3,
            (CapsuleerProfession.Combat, HullClass.Frigate, TechOne, true) => 1,
            (CapsuleerProfession.Combat, HullClass.Destroyer or HullClass.Cruiser, TechOne, true) => 2,
            (
                CapsuleerProfession.Combat,
                HullClass.CombatBattlecruiser
                    or HullClass.AttackBattlecruiser
                    or HullClass.Battleship,
                TechOne,
                true
            ) => 3,
            (CapsuleerProfession.Combat, _, > 0, _) when Profession(hull) == CapsuleerProfession.Combat => 4,
            _ => 0,
        };

    public static CapsuleerProfession? Profession(HullClass hull) =>
        hull switch
        {
            HullClass.MiningBarge
            or HullClass.Exhumer
            or HullClass.ExpeditionFrigate
            or HullClass.IndustrialCommand
            or HullClass.CapitalIndustrial
            or HullClass.ExpeditionCommand => CapsuleerProfession.Mining,
            HullClass.Hauler
            or HullClass.Freighter
            or HullClass.JumpFreighter
            or HullClass.DeepSpaceTransport
            or HullClass.BlockadeRunner => CapsuleerProfession.Hauling,
            HullClass.Frigate
            or HullClass.Destroyer
            or HullClass.Cruiser
            or HullClass.CombatBattlecruiser
            or HullClass.AttackBattlecruiser
            or HullClass.Battleship
            or HullClass.AssaultFrigate
            or HullClass.Interceptor
            or HullClass.HeavyAssaultCruiser
            or HullClass.EliteBattleship
            or HullClass.CommandShip
            or HullClass.Interdictor
            or HullClass.HeavyInterdictionCruiser
            or HullClass.ElectronicAttack
            or HullClass.ForceRecon
            or HullClass.CombatReconShip
            or HullClass.BlackOps
            or HullClass.Marauder
            or HullClass.StealthBomber
            or HullClass.StrategicCruiser
            or HullClass.TacticalDestroyer
            or HullClass.CommandDestroyer
            or HullClass.FlagCruiser
            or HullClass.LogisticsFrigate
            or HullClass.Logistics
            or HullClass.Dreadnought
            or HullClass.Carrier
            or HullClass.ForceAuxiliary
            or HullClass.Supercarrier
            or HullClass.Titan
            or HullClass.LancerDreadnought
            or HullClass.CommandCarrier => CapsuleerProfession.Combat,
            _ => null,
        };

    public static CapsuleerTrainingFocus Next(CapsuleerTrainingFocus focus) =>
        focus switch
        {
            CapsuleerTrainingFocus.Work => CapsuleerTrainingFocus.Ship,
            CapsuleerTrainingFocus.Ship => CapsuleerTrainingFocus.Foundation,
            _ => CapsuleerTrainingFocus.Work,
        };

    public static IReadOnlyList<TrainingTarget> Work(CapsuleerProfession profession, bool factory, bool management) =>
        profession switch
        {
            CapsuleerProfession.Mining when factory =>
            [
                new((int)ReprocessingSkill.Reprocessing, 3),
                new((int)IndustrySkill.Industry, 3),
                .. Management(management),
                new((int)MiningSkill.Mining, 4),
                new((int)IndustrySkill.MassProduction, 2),
                new((int)ReprocessingSkill.ReprocessingEfficiency, 3),
                new((int)IndustrySkill.AdvancedIndustry, 3),
            ],
            CapsuleerProfession.Mining =>
            [
                new((int)MiningSkill.Mining, 4),
                new((int)MiningSkill.Astrogeology, 3),
                .. Management(management),
            ],
            CapsuleerProfession.Hauling =>
            [
                new((int)MarketSkill.Trade, 3),
                new((int)NavigationSkill.Navigation, 4),
                .. Management(management),
            ],
            CapsuleerProfession.Combat =>
            [
                new((int)CombatSkill.Gunnery, 4),
                new((int)DefenseSkill.Mechanics, 4),
                .. Management(management),
            ],
            _ => [],
        };

    private static TrainingTarget[] Management(bool active) => active ? [new(CorporationRules.ManagementSkill, 1)] : [];

    public static IReadOnlyList<TrainingTarget> Foundation { get; } =
        Array.AsReadOnly<TrainingTarget>([
            new((int)FittingSkill.CpuManagement, FoundationLevel),
            new((int)FittingSkill.PowerGridManagement, FoundationLevel),
            new((int)FittingSkill.CapacitorManagement, FoundationLevel),
            new((int)FittingSkill.CapacitorSystemsOperation, FoundationLevel),
            new((int)DefenseSkill.Mechanics, FoundationLevel),
            new((int)DefenseSkill.HullUpgrades, FoundationLevel),
            new((int)DefenseSkill.ShieldManagement, FoundationLevel),
            new((int)DefenseSkill.ShieldOperation, FoundationLevel),
            new((int)TargetingSkill.LongRangeTargeting, FoundationLevel),
            new((int)TargetingSkill.SignatureAnalysis, FoundationLevel),
            new((int)NavigationSkill.Navigation, FoundationLevel),
            new(EvasiveManeuvering, FoundationLevel),
            new(WarpCapacitorRules.WarpDriveOperationSkill, FoundationLevel),
            new(SpaceshipCommand, FoundationLevel),
        ]);
}
