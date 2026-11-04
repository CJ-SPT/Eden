using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public enum MissileSourceKind
{
    Pilot,
    Npc,
}

public interface INpcMissiles
{
    IReadOnlyList<NpcShot> Advance(long pilotId, long shipId, SpacePosition velocity, long now);
}

public static class NpcMissileRules
{
    public static MissileDrive Drive(MissileDrive drive, IReadOnlyDictionary<DogmaAttributeId, double> npc)
    {
        var result = drive with
        {
            MaxVelocity = drive.MaxVelocity * (float)npc.GetValueOrDefault(DogmaAttributeId.EntityMissileVelocityMultiplier, 1),
            LifetimeMilliseconds =
                drive.LifetimeMilliseconds * npc.GetValueOrDefault(DogmaAttributeId.EntityMissileFlightTimeMultiplier, 1),
        };
        MissileFlightRules.Validate(result);
        return result;
    }

    public static MissileWarhead Warhead(
        IReadOnlyDictionary<DogmaAttributeId, double> charge,
        IReadOnlyDictionary<DogmaAttributeId, double> npc
    )
    {
        var attributes = charge.ToDictionary();
        attributes[DogmaAttributeId.ExplosionRadius] =
            charge.GetValueOrDefault(DogmaAttributeId.ExplosionRadius)
            * npc.GetValueOrDefault(DogmaAttributeId.EntityMissileExplosionRadiusMultiplier, 1);
        attributes[DogmaAttributeId.ExplosionVelocity] =
            charge.GetValueOrDefault(DogmaAttributeId.ExplosionVelocity)
            * npc.GetValueOrDefault(DogmaAttributeId.EntityMissileExplosionVelocityMultiplier, 1);
        attributes[DogmaAttributeId.MissileReductionExponent] =
            charge.GetValueOrDefault(DogmaAttributeId.MissileReductionExponent, 1)
            * npc.GetValueOrDefault(DogmaAttributeId.EntityMissileReductionMultiplier, 1);
        return MissileRules.Warhead(attributes, npc.GetValueOrDefault(DogmaAttributeId.MissileDamageMultiplier, 1));
    }
}
