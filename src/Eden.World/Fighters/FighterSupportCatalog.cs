using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;

namespace Eden.World.Fighters;

internal static class FighterSupportCatalog
{
    private const int SupportRole = 3;
    private const int CenobiteOne = 37599;
    private const int CenobiteTwo = 40568;
    private const int ScarabOne = 40345;
    private const int ScarabTwo = 40569;
    private const int SirenOne = 40346;
    private const int SirenTwo = 40570;
    private const int DromiOne = 40347;
    private const int DromiTwo = 40571;
    private const int StandupSirenOne = 47037;
    private const int StandupCenobiteOne = 47131;
    private const int StandupScarabOne = 47132;
    private const int StandupDromiOne = 47133;
    private const int StandupCenobiteTwo = 47134;
    private const int StandupScarabTwo = 47135;
    private const int StandupSirenTwo = 47136;
    private const int StandupDromiTwo = 47137;

    internal static DroneProfile Profile(
        CatalogItemDefinition type,
        ClientTypes? source,
        IReadOnlyDictionary<DogmaAttributeId, double> values
    )
    {
        if (
            source?.Published != 1
            || values.GetValueOrDefault(DogmaAttributeId.FighterSquadronRole) != SupportRole
            || type.Mass is not > 0
            || type.Radius is not > 0
            || source.Volume is not > 0
        )
        {
            throw new InvalidDataException("Invalid support fighter catalog data.");
        }
        var support = type.Id switch
        {
            CenobiteOne or CenobiteTwo or StandupCenobiteOne or StandupCenobiteTwo => Active(
                UtilityDroneKind.Neutralizing,
                DogmaAttributeId.FighterNeutralizeOptimal,
                DogmaAttributeId.FighterNeutralizeDuration,
                DogmaAttributeId.FighterNeutralizeAmount,
                DogmaAttributeId.FighterNeutralizeFalloff
            ),
            SirenOne or SirenTwo or StandupSirenOne or StandupSirenTwo => Active(
                UtilityDroneKind.WarpDisrupting,
                DogmaAttributeId.FighterDisruptRange,
                DogmaAttributeId.FighterDisruptDuration,
                DogmaAttributeId.FighterDisruptStrength
            ),
            DromiOne or DromiTwo or StandupDromiOne or StandupDromiTwo => Active(
                UtilityDroneKind.Webifying,
                DogmaAttributeId.FighterWebRange,
                DogmaAttributeId.FighterWebDuration,
                DogmaAttributeId.FighterWebStrength,
                DogmaAttributeId.FighterWebFalloff
            ),
            ScarabOne or ScarabTwo or StandupScarabOne or StandupScarabTwo => Jam(),
            _ => throw new InvalidDataException("Unsupported support fighter ability."),
        };
        float speed = (float)Positive(DogmaAttributeId.MaxVelocity);
        return new(
            type.Id,
            source.Volume.Value,
            0,
            type.Mass.Value,
            (float)type.Radius.Value,
            (float)Positive(DogmaAttributeId.Agility),
            speed,
            speed,
            Positive(DogmaAttributeId.FighterSquadronOrbitRange),
            support.Range,
            support.Falloff,
            0,
            0,
            support.DurationMilliseconds,
            DamageVector.None,
            ShipHealth.DefenseFrom(values),
            CombatDroneKind.Utility,
            support,
            FighterGroup: type.GroupId
        );

        double Value(DogmaAttributeId id) => values.GetValueOrDefault(id);
        double Positive(DogmaAttributeId id)
        {
            double value = Value(id);
            return double.IsFinite(value) && value > 0 ? value : throw new InvalidDataException("Invalid support fighter dogma attribute.");
        }

        UtilityDroneProfile Active(
            UtilityDroneKind kind,
            DogmaAttributeId range,
            DogmaAttributeId duration,
            DogmaAttributeId amount,
            DogmaAttributeId falloff = default
        ) => new(kind, Positive(range), Positive(duration), Value(amount), EffectMilliseconds: Positive(duration), Falloff: Value(falloff));

        UtilityDroneProfile Jam()
        {
            var duration = Positive(DogmaAttributeId.FighterEcmDuration);
            var optimal = Positive(DogmaAttributeId.FighterEcmOptimal);
            var falloff = Positive(DogmaAttributeId.FighterEcmFalloff);
            NpcEwarPower power = new(
                Jamming: new(
                    Positive(DogmaAttributeId.FighterEcmRadar),
                    Positive(DogmaAttributeId.FighterEcmLadar),
                    Positive(DogmaAttributeId.FighterEcmMagnetometric),
                    Positive(DogmaAttributeId.FighterEcmGravimetric)
                )
            );
            ElectronicWarfareModule module = new(
                0,
                type.Id,
                DogmaEffect.PlayerEcm,
                NpcEwarKind.Jamming,
                duration,
                0,
                optimal,
                falloff,
                power
            );
            return new(UtilityDroneKind.Jamming, optimal, duration, 0, module, duration, Falloff: falloff);
        }
    }
}
