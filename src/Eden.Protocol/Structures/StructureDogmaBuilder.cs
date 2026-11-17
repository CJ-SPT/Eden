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
            Attributes(structure),
            new Dictionary<DogmaEffect, DogmaActiveEffect>(),
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );

    private IReadOnlyDictionary<DogmaAttributeId, double> Attributes(StructureSnapshot structure)
    {
        var attributes = fitting.Attributes(structure.Profile.TypeId).ToDictionary();
        var modules = structure.Modules.Where(module => module.Online).Select(module => fitting.Attributes(module.TypeId)).ToArray();
        attributes[DogmaAttributeId.CpuOutput] = structure.Profile.Cpu;
        attributes[DogmaAttributeId.PowerOutput] = structure.Profile.Power;
        attributes[DogmaAttributeId.CpuLoad] = modules.Sum(module => module.GetValueOrDefault(DogmaAttributeId.Cpu));
        attributes[DogmaAttributeId.PowerLoad] = modules.Sum(module => module.GetValueOrDefault(DogmaAttributeId.Power));
        return attributes;
    }

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

    internal DogmaItemInfo<double> PrimeModule(DockedPilot pilot, StructureSnapshot structure, InventoryItem module)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = fitting.Attributes(module.TypeId).ToDictionary();
        attributes[DogmaAttributeId.IsOnline] = module.Online ? 1d : 0d;
        if (structure.Modules.Single(m => m.ItemId == module.Id).HourlyFuel is int fuel)
        {
            attributes[DogmaAttributeId.ServiceModuleFuelAmount] = fuel;
        }
        Dictionary<DogmaEffect, DogmaActiveEffect> effects = [];
        if (module.Online)
        {
            effects[DogmaEffect.Online] = new(module.Id, pilot.PilotId, structure.Id, null, DogmaEffect.Online, now, null, 0);
        }
        return new(new DogmaItemKey.Item(module.Id), InventoryRow.From(module), attributes, effects, now);
    }

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
            foreach (
                var module in structures
                    .Inventory(pilot.PilotId, structure.Id)
                    .Where(item => structure.Modules.Any(m => m.ItemId == item.Id))
            )
            {
                info[module.Id] = PrimeModule(pilot, structure, module);
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
