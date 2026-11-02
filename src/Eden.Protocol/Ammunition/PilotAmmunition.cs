using Eden.Protocol.Docking;

namespace Eden.Protocol.Ammunition;

public sealed record Magazine(long ModuleId, long ShipId, int FlagId, int TypeId, int Quantity, long ReadyAt);
public sealed record MagazineChange(Magazine? Previous, Magazine? Current);
public sealed record AmmunitionTransfer(IReadOnlyList<InventoryChange> Items, IReadOnlyList<MagazineChange> Magazines,
    double ReloadMilliseconds = 0);

public interface IPilotAmmunition
{
    IReadOnlyList<Magazine> Read(long pilotId, long shipId);
    AmmunitionTransfer Load(long pilotId, long shipId, IReadOnlyList<long> moduleIds, IReadOnlyList<long> chargeIds, long sourceId, long now);
    AmmunitionTransfer Unload(long pilotId, long shipId, IReadOnlyList<long> moduleIds, long destinationId, int flagId, int? quantity, long now);
}

public enum HybridChargeType
{
    IronSmall = 215,
    AntimatterSmall = 222,
}

public static class AmmunitionDefinitions
{
    public const int HybridChargeGroup = 85;
    public const int SmallChargeSize = 1;
    public const int TechOne = 1;
}
