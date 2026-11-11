using System.Text.Json;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyWorldNpc(
    long Id,
    double? AcquisitionRange,
    Eden.Gameplay.Combat.NpcAnchorKind AnchorKind,
    double ArmorDamage,
    long ArmorRepairAt,
    int BeltId,
    int CorporationId,
    long? DestroyedAt,
    long EncounterId,
    int FactionId,
    double? HomeX,
    double? HomeY,
    double? HomeZ,
    long MotionAt,
    string? Name,
    Eden.Gameplay.Combat.NpcNavigationState Navigation,
    long RepairRevision,
    long? RetiredAt,
    Eden.Gameplay.Combat.NpcRole Role,
    double ShieldCharge,
    long ShieldRepairAt,
    int SpawnSlot,
    double StructureDamage,
    int SystemId,
    int TypeId,
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
    private static void ProjectWorldNpc(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyWorldNpc>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(c => c.Value is null).Select(c => c.Key).ToArray();
            if (removed.Length != 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldNpc
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldNpc.Id=json_extract(value,'$.Id')
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
                    INSERT INTO WorldNpc(
                        Id,AcquisitionRange,AnchorKind,ArmorDamage,ArmorRepairAt,BeltId,CorporationId,DestroyedAt,
                        EncounterId,FactionId,HomeX,HomeY,HomeZ,MotionAt,Name,Navigation,RepairRevision,RetiredAt,Role,
                        ShieldCharge,ShieldRepairAt,SpawnSlot,StructureDamage,SystemId,TypeId,UpdatedAt,VelocityX,VelocityY,
                        VelocityZ,X,Y,Z
                    )
                    SELECT
                        json_extract(value,'$.Id'),json_extract(value,'$.AcquisitionRange'),
                        json_extract(value,'$.AnchorKind'),json_extract(value,'$.ArmorDamage'),
                        json_extract(value,'$.ArmorRepairAt'),json_extract(value,'$.BeltId'),
                        json_extract(value,'$.CorporationId'),json_extract(value,'$.DestroyedAt'),
                        json_extract(value,'$.EncounterId'),json_extract(value,'$.FactionId'),json_extract(value,'$.HomeX'),
                        json_extract(value,'$.HomeY'),json_extract(value,'$.HomeZ'),json_extract(value,'$.MotionAt'),
                        json_extract(value,'$.Name'),json_extract(value,'$.Navigation'),
                        json_extract(value,'$.RepairRevision'),json_extract(value,'$.RetiredAt'),
                        json_extract(value,'$.Role'),json_extract(value,'$.ShieldCharge'),
                        json_extract(value,'$.ShieldRepairAt'),json_extract(value,'$.SpawnSlot'),
                        json_extract(value,'$.StructureDamage'),json_extract(value,'$.SystemId'),
                        json_extract(value,'$.TypeId'),json_extract(value,'$.UpdatedAt'),json_extract(value,'$.VelocityX'),
                        json_extract(value,'$.VelocityY'),json_extract(value,'$.VelocityZ'),json_extract(value,'$.X'),
                        json_extract(value,'$.Y'),json_extract(value,'$.Z')
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(Id) DO UPDATE SET
                        AcquisitionRange=excluded.AcquisitionRange,AnchorKind=excluded.AnchorKind,
                        ArmorDamage=excluded.ArmorDamage,ArmorRepairAt=excluded.ArmorRepairAt,BeltId=excluded.BeltId,
                        CorporationId=excluded.CorporationId,DestroyedAt=excluded.DestroyedAt,
                        EncounterId=excluded.EncounterId,FactionId=excluded.FactionId,HomeX=excluded.HomeX,
                        HomeY=excluded.HomeY,HomeZ=excluded.HomeZ,MotionAt=excluded.MotionAt,Name=excluded.Name,
                        Navigation=excluded.Navigation,RepairRevision=excluded.RepairRevision,RetiredAt=excluded.RetiredAt,
                        Role=excluded.Role,ShieldCharge=excluded.ShieldCharge,ShieldRepairAt=excluded.ShieldRepairAt,
                        SpawnSlot=excluded.SpawnSlot,StructureDamage=excluded.StructureDamage,SystemId=excluded.SystemId,
                        TypeId=excluded.TypeId,UpdatedAt=excluded.UpdatedAt,VelocityX=excluded.VelocityX,
                        VelocityY=excluded.VelocityY,VelocityZ=excluded.VelocityZ,X=excluded.X,Y=excluded.Y,Z=excluded.Z
                    """
                );
            }
        }
    }
}
