using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Combat;

internal sealed record ElectronicWarfareSpaceContact(ElectronicWarfareModule Module, long TargetId, long StartedAt);

internal static class PlayerEwarWire
{
    internal static DogmaEffect? Effect(string? name) =>
        name switch
        {
            "remoteECMFalloff" => DogmaEffect.PlayerEcm,
            "remoteSensorDampFalloff" => DogmaEffect.SensorDampener,
            "shipModuleTrackingDisruptor" => DogmaEffect.TrackingDisruptor,
            "shipModuleGuidanceDisruptor" => DogmaEffect.GuidanceDisruptor,
            "remoteTargetPaintFalloff" => DogmaEffect.TargetPainter,
            _ => null,
        };

    internal static DestinyCommand Graphic(long shipId, ElectronicWarfareSpaceContact contact, bool active) =>
        new DestinyCommand.TimedSpecialEffect(
            shipId,
            contact.Module.ItemId,
            contact.Module.TypeId,
            contact.TargetId,
            null,
            contact.Module.Effect switch
            {
                DogmaEffect.SensorDampener => "effects.SensorDampening",
                DogmaEffect.TrackingDisruptor => "effects.TrackingDisruption",
                DogmaEffect.TargetPainter => "effects.TargetPaint",
                _ => "effects.ElectronicAttributeModifyTarget",
            },
            true,
            active,
            active,
            contact.Module.DurationMilliseconds,
            contact.StartedAt
        );
}

internal sealed record PlayerEwarChanged(ElectronicWarfareContact Effect, bool Active)
    : PilotNotification(Active ? "OnEwarStart" : "OnEwarEnd")
{
    public override object?[] Arguments =>
        [Effect.SourceShipId, Effect.Module.ItemId, Effect.Target.Id, NpcEwarWire.Slot(Effect.Module.Kind)];
}

internal sealed record PlayerJamChanged(ElectronicWarfareContact Effect, bool Active)
    : PilotNotification(Active ? "OnJamStart" : "OnJamEnd")
{
    public override object?[] Arguments =>
        Active
            ?
            [
                Effect.SourceShipId,
                Effect.Module.ItemId,
                Effect.Target.Id,
                NpcEwarWire.Slot(Effect.Module.Kind),
                Effect.StartedAt,
                Effect.Module.DurationMilliseconds,
            ]
            : [Effect.SourceShipId, Effect.Module.ItemId, Effect.Target.Id, NpcEwarWire.Slot(Effect.Module.Kind)];
}
