namespace Eden.Protocol.Combat;

internal static class CombatWireDefinitions
{
    public const string CivilianWeaponEffect = "targetAttack";
    public const string AmmoWeaponEffect = "projectileFired";
    public const string DisintegratorEffect = "targetDisintegratorAttack";
    public const string VortonEffect = "ChainLightning";

    public static bool Handles(string name) => name is CivilianWeaponEffect or AmmoWeaponEffect or DisintegratorEffect or VortonEffect;
}
