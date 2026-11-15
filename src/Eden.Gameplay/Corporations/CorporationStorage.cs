using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Corporations;

public sealed record CorporationOffice(long Id, int CorporationId, int StationId, int SystemId, long RentedAt, long FeeCents);

public sealed record CorporationStorageChange(
    long ActorId,
    CorporationOffice Office,
    bool RentalChanged,
    IReadOnlyList<InventoryChange> Items
);

public interface ICorporationStorage
{
    IReadOnlyList<CorporationOffice> Offices(long actorId);
    IReadOnlyList<int> Tenants(long actorId);
    int EmptyOffices(long actorId);
    long Quote(long actorId);
    CorporationOffice Rent(long actorId, long quotedCents);
    CorporationOffice Unrent(long actorId);
    CorporationOffice Office(long actorId, long officeId);
    IReadOnlyList<InventoryItem> List(long actorId, long officeId, int? flag);
    IReadOnlyList<InventoryItem> Assets(long actorId, int corporationId, int stationId);
    IReadOnlyList<InventoryChange> Move(long actorId, long destinationId, InventoryMoveRequest request);
    IReadOnlyList<InventoryChange> Stack(long actorId, long officeId, int flag);
    bool CanObserve(long actorId, long officeId, int flag);
    IReadOnlyList<InventoryItem> Deliveries(long actorId);
}

public static class CorporationStorageRules
{
    // Build 3662168 inventorycommon.const.flagCorpSAG1..7 and appConst hangar role families.
    public const int FirstFlag = 115;
    public const int LastFlag = 121;
    public const int DivisionCount = 7;

    // Build 3662168 inventorycommon.const includes Goals Deliveries in flagCorpSAGs, at division index 14.
    public const int GoalDeliveriesFlag = 184;
    public const int MarketDeliveriesContainer = 10012;
    public const int MarketDeliveriesFlag = 62;
    public const int MaximumBatch = 1000;
    public const long FirstOfficeId = 66000000;
    public const long LastOfficeId = 67999999;

    // Explicit Eden fallback policy: the catalog does not contain the live station rental quote.
    // Saved once per world; dynamic rent and recurring invoices belong to billing work.
    public const long DefaultRentalFeeCents = 1000000;
    public const int DefaultOfficeSlots = 24;

    public static bool IsDivision(int flag) => flag is >= FirstFlag and <= LastFlag;

    public static bool IsHangarFlag(int flag) => IsDivision(flag) || flag == GoalDeliveriesFlag;

    public static bool CanQuery(CorporationMemberAccess member, int station, int headquarters, int flag) =>
        Has(member, station, headquarters, flag, CorporationRole.HangarCanQuery1);

    public static bool CanTake(CorporationMemberAccess member, int station, int headquarters, int flag) =>
        Has(member, station, headquarters, flag, CorporationRole.HangarCanTake1);

    private static bool Has(CorporationMemberAccess member, int station, int headquarters, int flag, CorporationRole first)
    {
        if (flag == GoalDeliveriesFlag)
        {
            return member.Roles.Has(
                first == CorporationRole.HangarCanQuery1 ? CorporationRole.DeliveriesHangarQuery : CorporationRole.DeliveriesHangarTake
            );
        }
        if (!IsDivision(flag))
        {
            return false;
        }
        long roles = member.Roles.At(station, headquarters, member.BaseId);
        return (roles & (long)CorporationRole.Director) != 0 || (roles & ((long)first << (flag - FirstFlag))) != 0;
    }
}
