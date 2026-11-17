using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class StructureDogmaBuilder(IPilotStructures structures, IPilotFitting fitting, ShipDogmaBuilder ships, TimeProvider clock)
{
    internal DogmaItemInfo<double> Prime(StructureSnapshot structure) =>
        new(
            new DogmaItemKey.Item(structure.Id),
            Item(structure),
            fitting.Attributes(structure.Profile.TypeId),
            new Dictionary<DogmaEffect, DogmaActiveEffect>(),
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );

    internal static InventoryRow Item(StructureSnapshot structure) =>
        new(
            structure.Id,
            structure.Profile.TypeId,
            structure.CorporationId,
            structure.SystemId,
            (int)InventoryFlag.AutoFit,
            InventoryWireDefinitions.AssembledQuantity,
            structure.Profile.GroupId,
            structure.Profile.CategoryId
        );

    internal DogmaAllInfo AllInfo(DockedPilot pilot, StructureSnapshot structure, bool controlling)
    {
        var original = ships.AllInfo(pilot);
        Dictionary<long, DogmaItemInfo<double>> info = new() { [structure.Id] = Prime(structure) };
        Dictionary<long, DogmaInstanceState> instances = new()
        {
            [structure.Id] = new(structure.Id, true),
            [pilot.PilotId] = new(pilot.PilotId),
        };
        if (controlling)
        {
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            foreach (
                var module in structures
                    .Inventory(pilot.PilotId, structure.Id)
                    .Where(item => InventoryDefinitions.IsFittedSlot(item.FlagId) || structure.Modules.Any(m => m.ItemId == item.Id))
            )
            {
                var attributes = fitting.Attributes(module.TypeId).ToDictionary(pair => pair.Key, pair => pair.Value);
                attributes[DogmaAttributeId.IsOnline] = module.Online ? 1d : 0d;
                Dictionary<DogmaEffect, DogmaActiveEffect> effects = [];
                if (module.Online)
                {
                    effects[DogmaEffect.Online] = new(module.Id, pilot.PilotId, structure.Id, null, DogmaEffect.Online, now, null, 0);
                }
                info[module.Id] = new(new DogmaItemKey.Item(module.Id), InventoryRow.From(module), attributes, effects, now);
                instances[module.Id] = new(module.Id, module.Online);
            }
        }
        return original with
        {
            StructureInfo = info,
            ShipInfo = controlling ? info : original.ShipInfo,
            ActiveShipId = controlling ? structure.Id : pilot.ShipId,
            ShipState = controlling
                ? new(instances, new Dictionary<long, IReadOnlyDictionary<int, MagazineQuantity>>())
                : original.ShipState,
            Character =
                controlling && original.Character.Item is { } item
                    ? original.Character with
                    {
                        Item = item with { LocationId = structure.Id },
                    }
                    : original.Character,
        };
    }
}
