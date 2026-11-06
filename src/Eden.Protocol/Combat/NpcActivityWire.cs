using Eden.Gameplay.Mining;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Combat;

internal static class NpcActivityWire
{
    internal static DestinyCommand Effect(NpcActivityContact activity, bool active) =>
        new DestinyCommand.TimedSpecialEffect(
            activity.SourceId,
            activity.SourceId,
            activity.TypeId,
            activity.TargetId,
            null,
            activity.Kind switch
            {
                NpcActivityKind.Mining => "effects.Laser",
                NpcActivityKind.ArmorRepair => "effects.RemoteArmourRepair",
                NpcActivityKind.ShieldRepair => "effects.ShieldTransfer",
                _ => throw new InvalidDataException("Unsupported NPC activity."),
            },
            false,
            active,
            active,
            activity.Duration,
            activity.StartedAt
        );
}
