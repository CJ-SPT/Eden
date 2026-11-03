using Eden.Gameplay.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Combat;

internal enum CombatHitQuality
{
    Miss = 0,
    Grazing = 1,
    Light = 2,
    Standard = 3,
    WellAimed = 4,
    Excellent = 5,
    Wrecking = 6,
}

internal static class CombatMessages
{
    public static IncomingCombatMessage Incoming(NpcShot shot) =>
        new(shot.Applied, HitQuality(shot.Quality), shot.NpcId, shot.TypeId, shot.Weapon.TurretTypeId, shot.Damage.Scale(shot.Quality));

    public static OutgoingCombatMessage Outgoing(CombatShot shot, long target) =>
        new(shot.Applied, HitQuality(shot.Quality), target, shot.Weapon.TypeId, shot.Weapon.Damage.Scale(shot.Quality));

    public static CombatHitQuality HitQuality(double multiplier)
    {
        // The client localizes tiers 1..6. These bands describe Eden's initial
        // shot-quality model; they do not assert Tranquility's server RNG.
        return multiplier switch
        {
            <= 0 => CombatHitQuality.Miss,
            < CombatDefinitions.GrazingDamageLimit => CombatHitQuality.Grazing,
            < CombatDefinitions.LightDamageLimit => CombatHitQuality.Light,
            < CombatDefinitions.StandardDamageLimit => CombatHitQuality.Standard,
            < CombatDefinitions.WellAimedDamageLimit => CombatHitQuality.WellAimed,
            < CombatDefinitions.WreckingMultiplier => CombatHitQuality.Excellent,
            _ => CombatHitQuality.Wrecking,
        };
    }
}
