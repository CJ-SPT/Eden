using Eden.Gameplay.Drones;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Drones;

internal static class DroneWorkWire
{
    internal static DestinyCommand Effect(DroneContact drone, DroneWorkEffect work, bool active) =>
        new DestinyCommand.TimedSpecialEffect(
            drone.Id,
            drone.Id,
            drone.Profile.TypeId,
            work.TargetId,
            null,
            work.Kind switch
            {
                UtilityDroneKind.ShieldRepair => "effects.ShieldTransfer",
                UtilityDroneKind.ArmorRepair => "effects.RemoteArmourRepair",
                UtilityDroneKind.HullRepair => "effects.RemoteHullRepair",
                UtilityDroneKind.Neutralizing => "effects.EnergyDestabilization",
                UtilityDroneKind.Jamming => "effects.ElectronicAttributeModifyTarget",
                UtilityDroneKind.Dampening => "effects.SensorDampening",
                UtilityDroneKind.TrackingDisruption => "effects.TrackingDisruption",
                UtilityDroneKind.Painting => "effects.TargetPaint",
                UtilityDroneKind.Webifying => "effects.ModifyTargetSpeed",
                UtilityDroneKind.WarpDisrupting => "effects.WarpScramble",
                UtilityDroneKind.Salvaging => "effects.Salvaging",
                _ => "effects.Laser",
            },
            drone.Profile.Utility?.Offensive == true || work.Kind == UtilityDroneKind.WarpDisrupting,
            active,
            false,
            work.DurationMilliseconds,
            work.StartedAt
        );
}
