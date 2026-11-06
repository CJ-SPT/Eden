using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Drones;

namespace Eden.Protocol.Flight;

internal static partial class Ballpark
{
    internal static BallparkAddition AddDrones(IEnumerable<DroneContact> drones, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0);
        writer.Write(stamp);
        var contacts = drones.ToArray();
        var slims = contacts.Select(d => WriteDrone(writer, d)).ToArray();
        return new(stream.ToArray(), slims, contacts.ToDictionary(d => d.Id, d => d.Health.DamageState()));
    }

    private static SlimItem WriteDrone(BinaryWriter writer, DroneContact drone)
    {
        var profile = drone.Profile;
        bool controlled = drone.ShipId != 0 && drone.TargetId != 0;
        var mode =
            !controlled ? NavigationMode.Stop
            : drone.Orbiting ? NavigationMode.Orbit
            : NavigationMode.Follow;
        WriteBallHeader(
            writer,
            drone.Id,
            (byte)mode,
            profile.Radius,
            drone.Position,
            (byte)(DestinyBallFlags.Free | DestinyBallFlags.Interactive | DestinyBallFlags.Massive)
        );
        writer.Write(profile.Mass);
        writer.Write((byte)0);
        writer.Write(-1L);
        writer.Write(drone.CorporationId);
        writer.Write(-1);
        writer.Write(drone.SpeedLimit);
        WritePosition(writer, drone.Velocity);
        writer.Write(profile.Agility);
        writer.Write(controlled ? 1f : 0f);
        writer.Write((sbyte)-1);
        if (controlled)
        {
            writer.Write(drone.TargetId);
            writer.Write((float)(drone.Activity == DroneActivity.ReturningToBay ? 0 : profile.OrbitRange));
        }
        return new DroneSlimItem(drone);
    }
}
