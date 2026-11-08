using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Combat;

internal abstract record CombatMessage(double Damage, CombatHitQuality HitQuality, int Weapon, DamageVector DamageAttributes)
{
    public abstract Dictionary<string, object?> ToBlue();

    protected Dictionary<long, object?> AttributeFields() =>
        new()
        {
            [(long)DogmaAttributeId.EmDamage] = DamageAttributes.Em,
            [(long)DogmaAttributeId.ExplosiveDamage] = DamageAttributes.Explosive,
            [(long)DogmaAttributeId.KineticDamage] = DamageAttributes.Kinetic,
            [(long)DogmaAttributeId.ThermalDamage] = DamageAttributes.Thermal,
        };
}

internal sealed record OutgoingCombatMessage(
    double Applied,
    CombatHitQuality Quality,
    long Target,
    int WeaponType,
    DamageVector Attributes,
    bool Banked = false
) : CombatMessage(Applied, Quality, WeaponType, Attributes)
{
    public override Dictionary<string, object?> ToBlue() =>
        new()
        {
            ["damage"] = Damage,
            ["hitQuality"] = (int)HitQuality,
            ["isBanked"] = Banked,
            ["attackType"] = "me",
            ["target"] = Target,
            ["weapon"] = Weapon,
            ["damageAttributes"] = AttributeFields(),
        };
}

internal sealed record IncomingCombatMessage(
    double Applied,
    CombatHitQuality Quality,
    long Source,
    int SourceType,
    int WeaponType,
    DamageVector Attributes
) : CombatMessage(Applied, Quality, WeaponType, Attributes)
{
    public override Dictionary<string, object?> ToBlue()
    {
        Dictionary<string, object?> fields = new()
        {
            ["damage"] = Damage,
            ["hitQuality"] = (int)HitQuality,
            ["isBanked"] = false,
            // The R templates use source names without requiring a weapon.
            // RD's miss template requires a valid item type for weapon.name.
            ["attackType"] = Weapon > 0 ? "otherPlayer" : "otherPlayerWeapons",
            ["source"] = Source,
            ["attackerID"] = Source,
            // An NPC owner is localized by type rather than capsuleer identity.
            ["owner"] = new object?[] { (int)MessageArgumentType.TypeId, SourceType },
            ["damageAttributes"] = AttributeFields(),
        };
        // The pinned logger calls evetypes.GetName whenever this key exists,
        // including when its value is None. Integrated NPC weapons omit it.
        if (Weapon > 0)
        {
            fields["weapon"] = Weapon;
        }
        return fields;
    }
}

internal sealed record DamageMessageNotification(CombatMessage Message) : PilotNotification("OnDamageMessage")
{
    public override object?[] Arguments => [Message.ToBlue()];
}

internal sealed record PilotIncomingCombatMessage(PilotDamage Hit)
    : CombatMessage(Hit.Applied, CombatMessages.HitQuality(Hit.Quality), Hit.WeaponType, Hit.Damage)
{
    public override Dictionary<string, object?> ToBlue() =>
        new()
        {
            ["damage"] = Damage,
            ["hitQuality"] = (int)HitQuality,
            ["isBanked"] = Hit.Banked,
            ["attackType"] = "otherPlayer",
            ["source"] = Hit.AttackerShipId,
            ["attackerID"] = Hit.AttackerShipId,
            ["owner"] = new object?[] { (int)MessageArgumentType.OwnerId, Hit.AttackerId },
            ["weapon"] = Weapon,
            ["damageAttributes"] = AttributeFields(),
        };
}
