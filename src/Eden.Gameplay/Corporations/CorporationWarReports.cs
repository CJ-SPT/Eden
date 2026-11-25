using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Corporations;

// Pinned war report kill group filters, independent of inventory group IDs.
public enum WarReportGroup
{
    Capsules = 0,
    Frigates = 1,
    Destroyers = 2,
    Cruisers = 3,
    Battleships = 4,
    Battlecruisers = 5,
    CapitalShips = 6,
    Industrials = 7,
    Starbases = 8,
    Structures = 9,
}

public sealed record WarReportGroupLosses(int AttackerShipLoss, decimal AttackerIskLoss, int DefenderShipLoss, decimal DefenderIskLoss);

public sealed record CorporationWarStatistics(
    IReadOnlyDictionary<int, int> ShipsKilled,
    IReadOnlyDictionary<int, decimal> IskKilled,
    IReadOnlyDictionary<int, WarReportGroupLosses> KillsByGroup
);

public sealed record CorporationWarKill(long WarId, ShipCombatReport Report, decimal IskLost, decimal IskDestroyed);

public interface ICorporationWarReports
{
    CorporationWarStatistics Read(long warId);
    IReadOnlyList<CorporationWarKill> Kills(long warId, int? ownerId, WarReportGroup? group);
    CorporationWarKill? Kill(long? warId, long killId, string hash);
}

public static class WarReportRules
{
    public static bool MatchesClientSide(int attackerId, bool attackerLoss, int finalCorporationId, int? finalAllianceId)
    {
        // Build 3662168's War Report classifies kills using the final blow's corporation and alliance IDs.
        bool attackerKill = attackerId == finalCorporationId || attackerId == finalAllianceId;
        return attackerKill != attackerLoss;
    }

    public static string Hash(ShipCombatReport report)
    {
        // Native shared killmail identity hashes decimal IDs and FILETIME without separators.
        string input =
            Format(report.Victim.CharacterId)
            + Format(report.FinalBlow.CharacterId)
            + Format(report.Victim.ShipTypeId)
            + Format(report.Time);
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(input)));

        static string Format(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "None";
    }

    public static WarReportGroup? Group(InventoryCategory category, int group) =>
        category switch
        {
            InventoryCategory.Structure => WarReportGroup.Structures,
            InventoryCategory.Starbase => WarReportGroup.Starbases,
            _ => (HullClass)group switch
            {
                HullClass.Capsule => WarReportGroup.Capsules,
                HullClass.Destroyer or HullClass.Interdictor or HullClass.CommandDestroyer or HullClass.TacticalDestroyer =>
                    WarReportGroup.Destroyers,
                HullClass.Cruiser
                or HullClass.ForceRecon
                or HullClass.HeavyInterdictionCruiser
                or HullClass.HeavyAssaultCruiser
                or HullClass.Logistics
                or HullClass.CombatReconShip
                or HullClass.StrategicCruiser
                or HullClass.FlagCruiser
                or HullClass.Yacht => WarReportGroup.Cruisers,
                HullClass.Battleship or HullClass.BlackOps or HullClass.Marauder => WarReportGroup.Battleships,
                HullClass.CombatBattlecruiser or HullClass.AttackBattlecruiser or HullClass.CommandShip or HullClass.ExpeditionCommand =>
                    WarReportGroup.Battlecruisers,
                HullClass.Titan
                or HullClass.Dreadnought
                or HullClass.Carrier
                or HullClass.Supercarrier
                or HullClass.CapitalIndustrial
                or HullClass.ForceAuxiliary
                or HullClass.LancerDreadnought
                or HullClass.CommandCarrier => WarReportGroup.CapitalShips,
                HullClass.Hauler
                or HullClass.MiningBarge
                or HullClass.Freighter
                or HullClass.IndustrialCommand
                or HullClass.DeepSpaceTransport
                or HullClass.Exhumer
                or HullClass.JumpFreighter
                or HullClass.BlockadeRunner => WarReportGroup.Industrials,
                HullClass.Shuttle
                or HullClass.Corvette
                or HullClass.Frigate
                or HullClass.AssaultFrigate
                or HullClass.CovertOps
                or HullClass.ElectronicAttack
                or HullClass.PrototypeExploration
                or HullClass.Interceptor
                or HullClass.StealthBomber
                or HullClass.ExpeditionFrigate
                or HullClass.LogisticsFrigate => WarReportGroup.Frigates,
                _ => null,
            },
        };
}
