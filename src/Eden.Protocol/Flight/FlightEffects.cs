using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.Defenses;
using Eden.Protocol.Docking;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;

namespace Eden.Protocol.Flight;

internal sealed class FlightEffects(Action<DestinyCommand> emit)
{
    private static bool Changed<T>(T[]? current, T[]? previous) =>
        !ReferenceEquals(current, previous) && ((current?.Length ?? 0) != 0 || (previous?.Length ?? 0) != 0);

    internal void Replay(ShipContact ship, ShipContact? previous)
    {
        if (ship.MicroJump != previous?.MicroJump)
        {
            if (previous?.MicroJump is { } ended)
            {
                emit(FlightSession.MicroJumpEffect(ship.Pilot.ShipId, ended, false));
            }
            if (ship.MicroJump is { } started)
            {
                emit(FlightSession.MicroJumpEffect(ship.Pilot.ShipId, started, true));
            }
        }
        if (ship.Cloaked && previous is null)
        {
            emit(new DestinyCommand.CloakBall(ship.Pilot.ShipId));
        }
        RefreshMining(ship, previous);
        RefreshWeapons(ship, previous);
        RefreshShieldBoosts(ship, previous);
        RefreshLocalRepairs(ship, previous);
        RefreshHardeners(ship, previous);
        RefreshSignatureSuppression(ship, previous);
        RefreshAssaultDamageControl(ship, previous);
        RefreshSupport(ship, previous);
        if (Changed(ship.Tackle, previous?.Tackle))
        {
            foreach (var effect in (previous?.Tackle ?? []).Except(ship.Tackle ?? []))
            {
                TackleEffect(ship.Pilot.ShipId, effect, false);
            }
            foreach (var effect in (ship.Tackle ?? []).Except(previous?.Tackle ?? []))
            {
                TackleEffect(ship.Pilot.ShipId, effect, true);
            }
        }
        if (Changed(ship.ElectronicWarfare, previous?.ElectronicWarfare))
        {
            foreach (var effect in (previous?.ElectronicWarfare ?? []).Except(ship.ElectronicWarfare ?? []))
            {
                ElectronicWarfareEffect(ship.Pilot.ShipId, effect, false);
            }
            foreach (var effect in (ship.ElectronicWarfare ?? []).Except(previous?.ElectronicWarfare ?? []))
            {
                ElectronicWarfareEffect(ship.Pilot.ShipId, effect, true);
            }
        }
        if (Changed(ship.RemoteRepairs, previous?.RemoteRepairs))
        {
            foreach (var repair in (previous?.RemoteRepairs ?? []).Except(ship.RemoteRepairs ?? []))
            {
                RemoteRepairEffect(ship.Pilot.ShipId, repair, false);
            }
            foreach (var repair in (ship.RemoteRepairs ?? []).Except(previous?.RemoteRepairs ?? []))
            {
                RemoteRepairEffect(ship.Pilot.ShipId, repair, true);
            }
        }
        RefreshPropulsion(ship, previous);
        if (Changed(ship.CapacitorEffects, previous?.CapacitorEffects))
        {
            foreach (var effect in (previous?.CapacitorEffects ?? []).Except(ship.CapacitorEffects ?? []))
            {
                CapacitorEffect(effect, false);
            }
            foreach (var effect in (ship.CapacitorEffects ?? []).Except(previous?.CapacitorEffects ?? []))
            {
                CapacitorEffect(effect, true);
            }
        }
    }

    internal void CapacitorEffect(CapacitorCycle effect, bool active) => emit(CapacitorWire.Graphic(effect, active));

    internal void ElectronicWarfareEffect(long shipId, ElectronicWarfareSpaceContact effect, bool active) =>
        emit(PlayerEwarWire.Graphic(shipId, effect, active));

    internal void PropulsionEffect(long shipId, PropulsionContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                shipId,
                effect.Module.ItemId,
                effect.Module.TypeId,
                shipId,
                null,
                PropulsionWireDefinitions.Graphic(effect.Module.Effect),
                false,
                active,
                active,
                effect.Module.DurationMilliseconds,
                effect.StartedAt
            )
        );

    internal void TackleEffect(long shipId, TackleContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                shipId,
                effect.Module.ItemId,
                effect.Module.TypeId,
                effect.TargetId,
                null,
                TackleWire.Graphic(effect.Module.Effect),
                TackleDefinitions.Targeted(effect.Module.Effect),
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
                effect.Graphic,
                false,
                active,
                active,
                effect.Duration,
                effect.StartedAt
            )
        );

    internal void RefreshMining(ShipContact ship, ShipContact? previous)
    {
        if (!Changed(ship.Mining, previous?.Mining))
        {
            return;
        }
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
        if (!Changed(ship.Weapons, previous?.Weapons))
        {
            return;
        }
        foreach (var effect in (previous?.Weapons ?? []).Except(ship.Weapons ?? []))
        {
            WeaponEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.Weapons ?? []).Except(previous?.Weapons ?? []))
        {
            WeaponEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    internal void RemoteRepairEffect(long shipId, RemoteRepairCycle repair, bool active)
    {
        foreach (string graphic in RemoteRepairWire.Graphics(repair.Module.Effect))
        {
            emit(
                new DestinyCommand.TimedSpecialEffect(
                    shipId,
                    repair.Module.ItemId,
                    repair.Module.TypeId,
                    repair.TargetId,
                    null,
                    graphic,
                    false,
                    active,
                    active,
                    repair.Module.DurationMilliseconds,
                    repair.StartedAt
                )
            );
        }
    }

    internal void LocalRepairEffect(long shipId, LocalRepairContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                shipId,
                effect.ModuleId,
                effect.TypeId,
                shipId,
                null,
                LocalRepairWireDefinitions.Graphic(effect.Layer),
                false,
                active,
                active,
                effect.Duration,
                effect.StartedAt
            )
        );

    internal void HardenerEffect(long shipId, HardenerContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                shipId,
                effect.Module.ItemId,
                effect.Module.TypeId,
                shipId,
                null,
                HardenerWireDefinitions.Graphic(effect.Module.Effect),
                false,
                active,
                active,
                effect.Module.DurationMilliseconds,
                effect.StartedAt
            )
        );

    internal void SignatureSuppressionEffect(long ship, SignatureSuppressionContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                ship,
                effect.Module.ItemId,
                effect.Module.TypeId,
                ship,
                null,
                SignatureSuppressionHandler.Graphic,
                false,
                active,
                active,
                effect.Module.DurationMilliseconds,
                effect.Schedule.StartedAt
            )
        );

    private void RefreshSignatureSuppression(ShipContact ship, ShipContact? previous)
    {
        if (!Changed(ship.SignatureSuppressions, previous?.SignatureSuppressions))
        {
            return;
        }
        foreach (var effect in (previous?.SignatureSuppressions ?? []).Except(ship.SignatureSuppressions ?? []))
        {
            SignatureSuppressionEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.SignatureSuppressions ?? []).Except(previous?.SignatureSuppressions ?? []))
        {
            SignatureSuppressionEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    internal void AssaultDamageControlEffect(long ship, AssaultDamageControlContact effect, bool active) =>
        emit(
            new DestinyCommand.TimedSpecialEffect(
                ship,
                effect.Module.ItemId,
                effect.Module.TypeId,
                ship,
                null,
                AssaultDamageControlHandler.Graphic,
                false,
                active,
                active,
                effect.Module.DurationMilliseconds,
                effect.Schedule.StartedAt
            )
        );

    private void RefreshAssaultDamageControl(ShipContact ship, ShipContact? previous)
    {
        if (!Changed(ship.AssaultDamageControls, previous?.AssaultDamageControls))
        {
            return;
        }
        foreach (var effect in (previous?.AssaultDamageControls ?? []).Except(ship.AssaultDamageControls ?? []))
        {
            AssaultDamageControlEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.AssaultDamageControls ?? []).Except(previous?.AssaultDamageControls ?? []))
        {
            AssaultDamageControlEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    private void RefreshHardeners(ShipContact ship, ShipContact? previous)
    {
        if (!Changed(ship.Hardeners, previous?.Hardeners))
        {
            return;
        }
        foreach (var effect in (previous?.Hardeners ?? []).Except(ship.Hardeners ?? []))
        {
            HardenerEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.Hardeners ?? []).Except(previous?.Hardeners ?? []))
        {
            HardenerEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    internal void SupportEffect(long shipId, SupportContact effect, bool active)
    {
        if (SupportModuleWire.Graphic(effect.Module.Effect) is not { } graphic)
        {
            return;
        }
        emit(
            new DestinyCommand.TimedSpecialEffect(
                shipId,
                effect.Module.ItemId,
                effect.Module.TypeId,
                effect.TargetId ?? shipId,
                null,
                graphic,
                false,
                active,
                active,
                effect.Module.DurationMilliseconds,
                effect.StartedAt
            )
        );
    }

    private void RefreshSupport(ShipContact ship, ShipContact? previous)
    {
        if (!Changed(ship.Support, previous?.Support))
        {
            return;
        }
        foreach (var effect in (previous?.Support ?? []).Except(ship.Support ?? []))
        {
            SupportEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.Support ?? []).Except(previous?.Support ?? []))
        {
            SupportEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    private void RefreshLocalRepairs(ShipContact ship, ShipContact? previous)
    {
        if (!Changed(ship.LocalRepairs, previous?.LocalRepairs))
        {
            return;
        }
        foreach (var effect in (previous?.LocalRepairs ?? []).Except(ship.LocalRepairs ?? []))
        {
            LocalRepairEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.LocalRepairs ?? []).Except(previous?.LocalRepairs ?? []))
        {
            LocalRepairEffect(ship.Pilot.ShipId, effect, true);
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
        if (!Changed(ship.ShieldBoosts, previous?.ShieldBoosts))
        {
            return;
        }
        foreach (var effect in (previous?.ShieldBoosts ?? []).Except(ship.ShieldBoosts ?? []))
        {
            ShieldBoostEffect(ship.Pilot.ShipId, effect, false);
        }
        foreach (var effect in (ship.ShieldBoosts ?? []).Except(previous?.ShieldBoosts ?? []))
        {
            ShieldBoostEffect(ship.Pilot.ShipId, effect, true);
        }
    }

    internal void WarpEffect(long id, bool start)
    {
        // The simulation command moves the ball. This separate toggle drives the pilot's tunnel and peer flash.
        emit(new DestinyCommand.SpecialEffect(id, null, null, null, null, "effects.Warping", false, start, true));
    }

    internal void DepartureEffect(long shipId, long gateId, int systemId, int durationMilliseconds)
    {
        GateActivity(gateId, arriving: false);
        emit(new DestinyCommand.JumpOut(shipId, gateId, systemId, durationMilliseconds));
    }

    internal void GateActivity(long gateId, bool arriving) => emit(new DestinyCommand.GateActivity(gateId, arriving));

    internal void ArrivalEffect(long shipId) =>
        emit(new DestinyCommand.SpecialEffect(shipId, null, null, null, null, "effects.JumpIn", false, true, false));

    internal static void CorrectPosition(
        Action<StampedDestinyUpdate> send,
        long id,
        SpacePosition position,
        SpacePosition velocity,
        int stamp
    )
    {
        send(new(stamp, new DestinyCommand.SetBallPosition(id, position.X, position.Y, position.Z)));
        // This restores a missed sample. Ordinary prediction avoids setters that reset native interpolation and facing.
        send(new(stamp, new DestinyCommand.SetBallVelocity(id, velocity.X, velocity.Y, velocity.Z)));
    }
}
