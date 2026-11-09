using System.Text.Json.Serialization;
using Eden.Data.Catalog.Capsuleers;
using Eden.Gameplay.Inventory;

namespace Eden.Server.Capsuleers;

internal sealed record TrafficKillmail(
    [property: JsonPropertyName("killmail_id")] long Id,
    [property: JsonPropertyName("killmail_time")] DateTimeOffset Time,
    [property: JsonPropertyName("solar_system_id")] int SystemId,
    [property: JsonPropertyName("victim")] TrafficVictim? Victim
);

internal sealed record TrafficVictim(
    [property: JsonPropertyName("character_id")] long CharacterId,
    [property: JsonPropertyName("ship_type_id")] int HullTypeId,
    [property: JsonPropertyName("items")] TrafficKillItem[]? Items
);

internal sealed record TrafficKillItem(
    [property: JsonPropertyName("item_type_id")] int TypeId,
    [property: JsonPropertyName("flag")] int Flag,
    [property: JsonPropertyName("quantity_destroyed")] long Destroyed,
    [property: JsonPropertyName("quantity_dropped")] long Dropped,
    [property: JsonPropertyName("items")] TrafficKillItem[]? Items
)
{
    internal static bool FittingSlot(int flag) =>
        InventoryDefinitions.IsFittedSlot(flag) || flag is >= (int)InventoryFlag.SubsystemSlot0 and <= (int)InventoryFlag.SubsystemSlot3;

    internal static TrafficEquipment[] Equipment(IEnumerable<TrafficKillItem> items)
    {
        List<TrafficEquipment> result = [];
        foreach (var item in items.Where(item => FittingSlot(item.Flag)))
        {
            long quantity = checked(item.Destroyed + item.Dropped);
            result.Add(new(item.TypeId, item.Flag, checked((int)quantity)));
            foreach (var nested in item.Items ?? [])
            {
                result.Add(new(nested.TypeId, item.Flag, checked((int)(nested.Destroyed + nested.Dropped))));
                if (nested.Items is { Length: > 0 })
                {
                    throw new InvalidDataException("Unexpected nested fitted inventory.");
                }
            }
        }
        return result.OrderBy(item => item.Flag).ThenBy(item => item.TypeId).ToArray();
    }
}

internal sealed record TrafficCharacter(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category
);
