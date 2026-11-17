using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class StructureDogmaBuilder(
    IPilotStructures structures,
    IPilotFitting fitting,
    ShipDogmaBuilder ships,
    TimeProvider clock,
    Eden.Protocol.Ammunition.MagazineWire magazines
)
{
    internal DogmaItemInfo<double> Prime(StructureSnapshot structure) =>
        new(
            new DogmaItemKey.Item(structure.Id),
            Item(structure),
            Attributes(structure),
            new Dictionary<DogmaEffect, DogmaActiveEffect>(),
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );

    internal void NotifyAttributes(Eden.Protocol.PilotSessions.PilotCallContext context, StructureSnapshot structure)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var hull = Prime(structure);
        var changes = hull
            .Attributes.Select(attribute => new ModuleAttributeChange(context.Pilot.PilotId, hull.Key, attribute.Key, now, attribute.Value))
            .ToList();
        foreach (
            var item in structures
                .Inventory(context.Pilot.PilotId, structure.Id)
                .Where(item => structure.Modules.Concat(structure.Equipment).Any(module => module.ItemId == item.Id))
        )
        {
            var module = PrimeModule(context.Pilot, structure, item);
            changes.AddRange(
                module.Attributes.Select(attribute => new ModuleAttributeChange(
                    context.Pilot.PilotId,
                    module.Key,
                    attribute.Key,
                    now,
                    attribute.Value
                ))
            );
        }
        context.Notifications.Add(new ModuleAttributesChanged(changes));
    }

    private IReadOnlyDictionary<DogmaAttributeId, double> Attributes(StructureSnapshot structure)
    {
        var attributes = (structure.Fitting?.Attributes ?? fitting.Attributes(structure.Profile.TypeId)).ToDictionary();
        if (structure.Fitting is null)
        {
            var modules = structure.Modules.Where(module => module.Online).Select(module => fitting.Attributes(module.TypeId)).ToArray();
            attributes[DogmaAttributeId.CpuOutput] = structure.Profile.Cpu;
            attributes[DogmaAttributeId.PowerOutput] = structure.Profile.Power;
            attributes[DogmaAttributeId.CpuLoad] = modules.Sum(module => module.GetValueOrDefault(DogmaAttributeId.Cpu));
            attributes[DogmaAttributeId.PowerLoad] = modules.Sum(module => module.GetValueOrDefault(DogmaAttributeId.Power));
        }
        if (structure.Battle is { } battle)
        {
            attributes[DogmaAttributeId.ShieldCharge] = battle.Health.Shield;
            attributes[DogmaAttributeId.ArmorDamage] = structure.Profile.Armor - battle.Health.Armor;
            attributes[DogmaAttributeId.Damage] = structure.Profile.Hull - battle.Health.Structure;
        }
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
        var attributes = (structure.Fitting?.Equipment.GetValueOrDefault(module.Id) ?? fitting.Attributes(module.TypeId)).ToDictionary();
        attributes[DogmaAttributeId.IsOnline] = module.Online ? 1d : 0d;
        attributes[DogmaAttributeId.NextActivationTime] =
            structure.Magazines.SingleOrDefault(item => item.ModuleId == module.Id)?.ReadyAt ?? 0;
        if (structure.Modules.SingleOrDefault(m => m.ItemId == module.Id)?.HourlyFuel is int fuel)
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

    internal DogmaItemInfo<double> PrimeCharge(DockedPilot pilot, Eden.Gameplay.Ammunition.Magazine magazine) =>
        magazines.Prime(pilot.PilotId, magazine);

    internal DogmaAllInfo AllInfo(DockedPilot pilot, StructureSnapshot structure, bool controlling)
    {
        var original = ships.AllInfo(pilot);
        Dictionary<long, DogmaItemInfo<double>> info = new() { [structure.Id] = Prime(structure) };
        Dictionary<long, DogmaInstanceState> instances = new()
        {
            [structure.Id] = new(structure.Id, true),
            [pilot.PilotId] = new(pilot.PilotId),
        };
        Dictionary<long, IReadOnlyDictionary<int, MagazineQuantity>> quantities = [];
        if (controlling)
        {
            foreach (
                var module in structures
                    .Inventory(pilot.PilotId, structure.Id)
                    .Where(item => structure.Modules.Concat(structure.Equipment).Any(m => m.ItemId == item.Id))
            )
            {
                info[module.Id] = PrimeModule(pilot, structure, module);
                instances[module.Id] = new(module.Id, module.Online);
            }
            if (structure.Magazines.Count > 0)
            {
                quantities[structure.Id] = structure.Magazines.ToDictionary(
                    magazine => magazine.FlagId,
                    magazine => new MagazineQuantity(magazine)
                );
            }
        }
        return original with
        {
            StructureInfo = info,
            ShipInfo = controlling ? info : original.ShipInfo,
            Charges = controlling
                ? structure.Magazines.ToDictionary(
                    magazine => new DogmaItemKey.Charge(magazine.ShipId, magazine.FlagId, magazine.TypeId),
                    magazine => magazines.Prime(pilot.PilotId, magazine)
                )
                : original.Charges,
            ActiveShipId = controlling ? structure.Id : pilot.ShipId,
            ShipState = controlling ? new(instances, quantities) : original.ShipState,
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
