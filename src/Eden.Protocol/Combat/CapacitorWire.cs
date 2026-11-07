using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Combat;

internal static class CapacitorWire
{
    internal static DogmaEffect? Effect(string? name) =>
        name switch
        {
            "shipModuleRemoteCapacitorTransmitter" => DogmaEffect.RemoteCapacitorTransfer,
            "energyNeutralizerFalloff" => DogmaEffect.EnergyNeutralizer,
            "energyNosferatuFalloff" => DogmaEffect.EnergyNosferatu,
            _ => null,
        };

    internal static DestinyCommand Graphic(CapacitorCycle cycle, bool active) =>
        new DestinyCommand.TimedSpecialEffect(
            cycle.SourceShipId,
            cycle.Module.ItemId,
            cycle.Module.TypeId,
            cycle.TargetId,
            null,
            cycle.Module.Kind switch
            {
                CapacitorModuleKind.Transfer => "effects.EnergyTransfer",
                CapacitorModuleKind.Neutralizer => "effects.EnergyDestabilization",
                _ => "effects.EnergyVampire",
            },
            cycle.Module.Kind != CapacitorModuleKind.Transfer,
            active,
            active,
            cycle.Module.DurationMilliseconds,
            cycle.StartedAt
        );
}

internal sealed record CapacitorEwarChanged(CapacitorCycle Cycle, bool Active) : PilotNotification(Active ? "OnEwarStart" : "OnEwarEnd")
{
    // Build 3662168 dogma.effects and buffBarConst distinguish neut and vampire slots.
    public override object?[] Arguments =>
        [
            Cycle.SourceShipId,
            Cycle.Module.ItemId,
            Cycle.TargetId,
            Cycle.Module.Kind == CapacitorModuleKind.Neutralizer ? "ewEnergyNeut" : "ewEnergyVampire",
        ];
}
