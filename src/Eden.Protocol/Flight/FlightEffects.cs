using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;

namespace Eden.Protocol.Flight;

internal sealed class FlightEffects(Action<DestinyCommand> emit)
{
    internal void Replay(ShipContact ship, ShipContact? previous)
    {
        RefreshMining(ship, previous);
        RefreshWeapons(ship, previous);
        RefreshShieldBoosts(ship, previous);
        RefreshPropulsion(ship, previous);
    }

    internal void PropulsionEffect(long shipId, PropulsionContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                shipId,
                effect.Module.ItemId,
                effect.Module.TypeId,
                shipId,
                null,
                PropulsionWireDefinitions.EffectGraphic,
                false,
                active,
                active,
                effect.Module.DurationMilliseconds,
                effect.StartedAt
            )
        );

    internal void RefreshPropulsion(ShipContact ship, ShipContact? previous)
    {
        long id = ship.Pilot.ShipId;
        if (previous is not null && previous.Pilot.Flight != ship.Pilot.Flight)
        {
            emit(new DestinyCommand.SetBallMass(id, ship.Pilot.Flight.Mass));
            emit(new DestinyCommand.SetMaxSpeed(id, (double)ship.Pilot.Flight.MaxSpeed));
        }
        if (previous?.Propulsion == ship.Propulsion)
        {
            return;
        }
        if (previous?.Propulsion is { } stopped)
        {
            PropulsionEffect(id, stopped, false);
        }
        if (ship.Propulsion is { } started)
        {
            PropulsionEffect(id, started, true);
        }
    }

    internal void MiningEffect(long shipId, MiningContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                shipId,
                effect.ModuleId,
                effect.TypeId,
                effect.Target,
                null,
                "effects.Laser",
                false,
                active,
                active,
                effect.Duration,
                effect.StartedAt
            )
        );

    internal void RefreshMining(ShipContact ship, ShipContact? previous)
    {
        foreach (var effect in (previous?.Mining ?? []).Except(ship.Mining ?? []))
        {
            MiningEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.Mining ?? []).Except(previous?.Mining ?? []))
        {
            MiningEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    internal void WeaponEffect(long shipId, WeaponContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                shipId,
                effect.ModuleId,
                effect.TypeId,
                effect.Target,
                effect.ChargeTypeId,
                effect.Graphic,
                true,
                active,
                active,
                effect.Duration,
                effect.StartedAt
            )
        );

    internal void RefreshWeapons(ShipContact ship, ShipContact? previous)
    {
        foreach (var effect in (previous?.Weapons ?? []).Except(ship.Weapons ?? []))
        {
            WeaponEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.Weapons ?? []).Except(previous?.Weapons ?? []))
        {
            WeaponEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    internal void ShieldBoostEffect(long shipId, ShieldBoostContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                shipId,
                effect.ModuleId,
                effect.TypeId,
                shipId,
                null,
                ShieldBoostWireDefinitions.EffectGraphic,
                false,
                active,
                active,
                effect.Duration,
                effect.StartedAt
            )
        );

    internal void RefreshShieldBoosts(ShipContact ship, ShipContact? previous)
    {
        foreach (var effect in (previous?.ShieldBoosts ?? []).Except(ship.ShieldBoosts ?? []))
        {
            ShieldBoostEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.ShieldBoosts ?? []).Except(previous?.ShieldBoosts ?? []))
        {
            ShieldBoostEffect(ship.Pilot.ShipId, effect, true);
        }
    }
}
