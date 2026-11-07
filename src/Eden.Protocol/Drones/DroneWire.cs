using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Drones;

internal static class DroneWire
{
    internal static void NotifyControl(PilotCallContext context, DroneControl control, long now)
    {
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.PilotId),
                    DogmaAttributeId.MaxActiveDrones,
                    now,
                    control.MaximumDrones
                ),
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.PilotId),
                    DogmaAttributeId.DroneControlDistance,
                    now,
                    control.Range
                ),
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.DroneBandwidthLoad,
                    now,
                    control.UsedBandwidth
                ),
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.DroneIsAggressive,
                    now,
                    control.Settings.Aggressive ? 1 : 0
                ),
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.DroneFocusFire,
                    now,
                    control.Settings.FocusFire ? 1 : 0
                ),
            ])
        );
    }
}

internal sealed record DroneLaunchReply(IReadOnlyList<DroneLaunch> Launches) : BlueModel
{
    // eveMisc.LaunchFromShip expects original stack -> list of launched IDs.
    public override object ToBlue() => Launches.ToDictionary(l => l.StackId, l => (object)new BlueList<long>(l.DroneIds));
}

internal sealed record DroneCommandReply : BlueModel
{
    // HandleMultipleCallError consumes a dictionary of failures; an empty dictionary means success.
    public override object ToBlue() => new Dictionary<long, object?>();
}

internal sealed record DroneState(DroneContact Drone, bool Removed = false) : BlueModel
{
    // Park.SetState reads named row fields; live OnDroneStateChange takes seven positional arguments.
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["droneID"] = Drone.Id,
                ["ownerID"] = Drone.PilotId,
                ["controllerID"] = Drone.ShipId,
                ["activityState"] = (int)Drone.Activity,
                ["typeID"] = Drone.Profile.TypeId,
                ["controllerOwnerID"] = Drone.PilotId,
                ["targetID"] = Drone.TargetId == 0 ? null : Drone.TargetId,
            }
        );

    internal object?[] Arguments =>
        [
            Drone.Id,
            Removed ? 0L : Drone.PilotId,
            Removed ? 0L : Drone.ShipId,
            Removed ? -1 : (int)Drone.Activity,
            Drone.Profile.TypeId,
            Removed ? 0L : Drone.PilotId,
            Drone.TargetId == 0 || Removed ? null : Drone.TargetId,
        ];
}

internal sealed record DroneSlimItem(DroneContact Drone) : SlimItem
{
    public override object ToBlue()
    {
        var fields = Fields(Drone.Id, Drone.Profile.TypeId, Drone.PilotId, DroneRules.CombatDroneGroup, (int)InventoryCategory.Drone);
        fields["controllerID"] = Drone.ShipId == 0 ? null : Drone.ShipId;
        fields["controllerOwnerID"] = Drone.ShipId == 0 ? null : Drone.PilotId;
        fields["corpID"] = Drone.CorporationId;
        return Wrap(fields);
    }
}
