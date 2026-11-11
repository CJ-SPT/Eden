using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldDrone(
    long ItemId,
    Eden.Gameplay.Drones.DroneActivity Activity,
    double ArmorDamage,
    long CommandedAt,
    long ControllerShipId,
    int CorporationId,
    long DelegateFleetId,
    long DelegateMembership,
    long DelegatePilotId,
    long DelegateShipId,
    Eden.Gameplay.Drones.DroneDelegationMode DelegationMode,
    bool Orbiting,
    long OwnerMembership,
    long PilotId,
    long ReactionRevision,
    long ReadyAt,
    double Shield,
    long ShotAt,
    long ShotTargetId,
    float SpeedLimit,
    double StructureDamage,
    int? SystemId,
    long TargetId,
    long UpdatedAt,
    double VelocityX,
    double VelocityY,
    double VelocityZ,
    double X,
    double Y,
    double Z
);

internal sealed partial class ResidentEconomy
{
    private static void ProjectWorldDrone(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldDrone>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldDrone
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldDrone.ItemId=json_extract(value,'$.Id')
                    )
                    """
                );
            }
            var rows = batch.Where(c => c.Value is not null).Select(c => c.Value).ToArray();
            if (rows.Length != 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldDrone(
                        ItemId,Activity,ArmorDamage,CommandedAt,ControllerShipId,CorporationId,DelegateFleetId,
                        DelegateMembership,DelegatePilotId,DelegateShipId,DelegationMode,Orbiting,OwnerMembership,PilotId,
                        ReactionRevision,ReadyAt,Shield,ShotAt,ShotTargetId,SpeedLimit,StructureDamage,SystemId,TargetId,
                        UpdatedAt,VelocityX,VelocityY,VelocityZ,X,Y,Z
                    )
                    SELECT
                        json_extract(value,'$.ItemId'),json_extract(value,'$.Activity'),json_extract(value,'$.ArmorDamage'),
                        json_extract(value,'$.CommandedAt'),json_extract(value,'$.ControllerShipId'),
                        json_extract(value,'$.CorporationId'),json_extract(value,'$.DelegateFleetId'),
                        json_extract(value,'$.DelegateMembership'),json_extract(value,'$.DelegatePilotId'),
                        json_extract(value,'$.DelegateShipId'),json_extract(value,'$.DelegationMode'),
                        json_extract(value,'$.Orbiting'),json_extract(value,'$.OwnerMembership'),
                        json_extract(value,'$.PilotId'),json_extract(value,'$.ReactionRevision'),
                        json_extract(value,'$.ReadyAt'),json_extract(value,'$.Shield'),json_extract(value,'$.ShotAt'),
                        json_extract(value,'$.ShotTargetId'),json_extract(value,'$.SpeedLimit'),
                        json_extract(value,'$.StructureDamage'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.TargetId'),json_extract(value,'$.UpdatedAt'),
                        json_extract(value,'$.VelocityX'),json_extract(value,'$.VelocityY'),
                        json_extract(value,'$.VelocityZ'),json_extract(value,'$.X'),json_extract(value,'$.Y'),
                        json_extract(value,'$.Z')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(ItemId) DO UPDATE SET
                        Activity=excluded.Activity,ArmorDamage=excluded.ArmorDamage,CommandedAt=excluded.CommandedAt,
                        ControllerShipId=excluded.ControllerShipId,CorporationId=excluded.CorporationId,
                        DelegateFleetId=excluded.DelegateFleetId,DelegateMembership=excluded.DelegateMembership,
                        DelegatePilotId=excluded.DelegatePilotId,DelegateShipId=excluded.DelegateShipId,
                        DelegationMode=excluded.DelegationMode,Orbiting=excluded.Orbiting,
                        OwnerMembership=excluded.OwnerMembership,PilotId=excluded.PilotId,
                        ReactionRevision=excluded.ReactionRevision,ReadyAt=excluded.ReadyAt,Shield=excluded.Shield,
                        ShotAt=excluded.ShotAt,ShotTargetId=excluded.ShotTargetId,SpeedLimit=excluded.SpeedLimit,
                        StructureDamage=excluded.StructureDamage,SystemId=excluded.SystemId,TargetId=excluded.TargetId,
                        UpdatedAt=excluded.UpdatedAt,VelocityX=excluded.VelocityX,VelocityY=excluded.VelocityY,
                        VelocityZ=excluded.VelocityZ,X=excluded.X,Y=excluded.Y,Z=excluded.Z
                    """
                );
            }
        }
    }
}
