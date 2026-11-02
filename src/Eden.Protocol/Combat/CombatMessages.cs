using Eden.Protocol.Docking;

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
    // Build 3662168 godma.OnDamageMessage and logger.AddCombatMessageFromDict
    // consume this dictionary. "weapon" is a type ID; "target" is a ball ID.
    public static Dictionary<string, object?> Outgoing(CombatShot shot, long target) => new()
    {
        ["damage"] = shot.Applied,
        ["hitQuality"] = (int)HitQuality(shot.Quality),
        ["isBanked"] = false,
        ["attackType"] = "me",
        ["target"] = target,
        ["weapon"] = shot.Weapon.TypeId,
        ["damageAttributes"] = new Dictionary<long, object?>
        {
            [(long)DogmaAttributeId.EmDamage] = shot.Weapon.Damage.Em * shot.Quality,
            [(long)DogmaAttributeId.ExplosiveDamage] = shot.Weapon.Damage.Explosive * shot.Quality,
            [(long)DogmaAttributeId.KineticDamage] = shot.Weapon.Damage.Kinetic * shot.Quality,
            [(long)DogmaAttributeId.ThermalDamage] = shot.Weapon.Damage.Thermal * shot.Quality,
        },
    };

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
