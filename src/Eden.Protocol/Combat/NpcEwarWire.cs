using Eden.Gameplay.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Combat;

internal static class NpcEwarWire
{
    internal static string Slot(NpcEwarKind kind) =>
        kind switch
        {
            NpcEwarKind.Jamming => "electronic",
            NpcEwarKind.SensorDampening => "ewRemoteSensorDamp",
            NpcEwarKind.TrackingDisruption => "ewTrackingDisrupt",
            NpcEwarKind.TargetPainting => "ewTargetPaint",
            NpcEwarKind.EnergyNeutralization => "ewEnergyNeut",
            NpcEwarKind.Webifying => "webify",
            NpcEwarKind.WarpDisruption => "warpScrambler",
            _ => throw new InvalidDataException("Unsupported NPC EWAR slot."),
        };

    internal static string Graphic(NpcEwarKind kind) =>
        kind switch
        {
            NpcEwarKind.Jamming or NpcEwarKind.SensorDampening or NpcEwarKind.TrackingDisruption =>
                "effects.ElectronicAttributeModifyTarget",
            NpcEwarKind.TargetPainting => "effects.TargetPaint",
            NpcEwarKind.EnergyNeutralization => "effects.EnergyDestabilization",
            NpcEwarKind.Webifying => "effects.ModifyTargetSpeed",
            NpcEwarKind.WarpDisruption => "effects.WarpScramble",
            _ => throw new InvalidDataException("Unsupported NPC EWAR graphic."),
        };

    internal static DestinyCommand Effect(NpcEwarContact contact, bool active) =>
        new DestinyCommand.TimedSpecialEffect(
            contact.SourceId,
            contact.SourceId,
            contact.SourceTypeId,
            contact.TargetShipId,
            null,
            Graphic(contact.Ability.Kind),
            true,
            active,
            active,
            contact.Ability.DurationMilliseconds,
            contact.StartedAt
        );
}

internal sealed record NpcEwarChanged(NpcEwarContact Effect, bool Active) : PilotNotification(Active ? "OnEwarStart" : "OnEwarEnd")
{
    // NPCs have no inventory module. As with NPC missiles, the source ball is
    // the stable native module reference; the slot distinguishes its abilities.
    public override object?[] Arguments => [Effect.SourceId, Effect.SourceId, Effect.TargetShipId, NpcEwarWire.Slot(Effect.Ability.Kind)];
}
