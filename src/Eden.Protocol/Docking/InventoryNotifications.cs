using Eden.Gameplay.Dogma;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

internal sealed record ItemChanged(InventoryRow Item, IReadOnlyDictionary<InventoryColumn, long> Previous)
    : PilotNotification("OnItemChange")
{
    public override object?[] Arguments =>
        [
            Item.ToBlue(),
            Previous.ToDictionary(p => (long)p.Key, p => p.Key == InventoryColumn.Location ? (object?)p.Value : checked((int)p.Value)),
            null,
        ];
}

internal sealed record BlueprintsUpdated(long OwnerId) : PilotNotification("OnBlueprintsUpdated")
{
    public override object?[] Arguments => [OwnerId];
}

internal sealed record ModuleAttributeChange(long PilotId, DogmaItemKey Item, DogmaAttributeId Attribute, long Time, double Value)
{
    public object?[] ToBlue() => ["OnModuleAttributeChange", PilotId, Item.ToBlue(), (int)Attribute, Time, Value, null, Time];
}

internal sealed record ModuleAttributesChanged(IReadOnlyList<ModuleAttributeChange> Changes) : PilotNotification("OnModuleAttributeChanges")
{
    public override object?[] Arguments => [Changes.Select(change => (object?)change.ToBlue()).ToList()];
}

internal sealed record GodmaItemPrimed(long ShipId, DogmaItemInfo<double> Item) : PilotNotification("OnGodmaPrimeItem")
{
    public override object?[] Arguments => [ShipId, Item.ToBlue()];
}

internal sealed record ChargeLoading(IReadOnlyList<long> Modules, int TypeId, double ReloadMilliseconds)
    : PilotNotification("OnChargeBeingLoadedToModule")
{
    public override object?[] Arguments => [Modules.Select(id => (object?)id).ToList(), TypeId, ReloadMilliseconds];
}

internal sealed record ShipEffectChanged(DogmaActiveEffect Effect, long Time, bool Start, bool Active, long? StopTime = null)
    : PilotNotification("OnGodmaShipEffect")
{
    public override object?[] Arguments =>
        [
            Effect.ModuleId,
            (int)Effect.Effect,
            Time,
            Start,
            Active,
            new object?[]
            {
                Effect.ModuleId,
                Effect.PilotId,
                Effect.ShipId,
                Effect.TargetId,
                null,
                Array.Empty<object?>(),
                (int)Effect.Effect,
            },
            Effect.StartTime,
            Effect.DurationMilliseconds is double duration ? (object)duration : InventoryWireDefinitions.InfiniteEffectDuration,
            Effect.Repeats,
            null,
            StopTime,
        ];
}
