using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable(InjectionType.Scoped)]
internal sealed class StructureWeaponHandler(
    IPilotStructureWeapons weapons,
    TimeProvider clock,
    CapacitorSignal capacitor,
    StructureWeaponEffects beams
) : IDisposable
{
    private StructureControlToken? _token;
    private readonly Dictionary<long, StructureWeaponCycle> _observed = [];

    private readonly Dictionary<long, CapacitorCycle> _neutralizers = [];
    private readonly Dictionary<long, StructureBeam> _beams = [];
    private Dictionary<long, StructureBeam> _visible = [];
    private Dictionary<long, StructureBeam> _nextVisible = [];
    private int _sourceSystem;

    internal static DogmaEffect? Effect(string name) =>
        name switch
        {
            MissileHandler.EffectName => DogmaEffect.UseMissiles,
            "structureModuleEffectStasisWebifier" => DogmaEffect.StructureStasisWebifier,
            "structureEnergyNeutralizerFalloff" => DogmaEffect.StructureEnergyNeutralizer,
            _ => null,
        };

    internal static DogmaActiveEffect Effect(StructureControlToken token, StructureWeaponCycle cycle) =>
        new(
            cycle.ModuleId,
            token.PilotId,
            token.StructureId,
            cycle.TargetId,
            cycle.Effect,
            cycle.StartedAt,
            cycle.DurationMilliseconds,
            cycle.Repeats
        );

    private void Notify(PilotCallContext context, StructureControlToken token, StructureWeaponCycle cycle, bool active)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (!active && _neutralizers.Remove(cycle.ModuleId, out var ended))
        {
            capacitor.Set(ended, false);
        }
        if (!active && _beams.Remove(cycle.ModuleId, out var beam))
        {
            beams.Set(_sourceSystem, beam, false);
            context.Flight.StructureBeamEffect(beam, false);
        }
        context.Notifications.Add(new ShipEffectChanged(Effect(token, cycle), now, active, active, active ? null : now));
    }

    internal void Advance(PilotCallContext context)
    {
        var token = context.StructureControl?.Token;
        if (_token != token)
        {
            if (_token is { } previous)
            {
                foreach (var cycle in _observed.Values)
                {
                    Notify(context, previous, cycle, false);
                }
            }
            _observed.Clear();
            _token = token;
        }
        RefreshBeams(context);
        if (token is null)
        {
            return;
        }
        try
        {
            var frame = weapons.Advance(token, context.Flight.CurrentStamp);
            foreach (var stopped in frame.Stopped)
            {
                Notify(context, token, stopped, false);
                _observed.Remove(stopped.ModuleId);
            }
            foreach (var start in frame.Started)
            {
                Started(context, token, start);
            }
        }
        catch (InvalidDataException)
        {
            context.ReleaseStructureControl();
        }
    }

    private void RefreshBeams(PilotCallContext context)
    {
        var active = beams.Read(context.Pilot.SystemId);
        if (active.Count == 0 && _visible.Count == 0)
        {
            return;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        _nextVisible.Clear();
        foreach (var beam in active)
        {
            if (
                beam.Cycle.ReadyAt > now
                && beam.SourceId != context.ControlledStructureId
                && context.Flight.SeesStructureEffect(beam.SourceId, beam.Cycle.TargetId)
            )
            {
                _nextVisible.Add(beam.Cycle.ModuleId, beam);
            }
        }
        foreach (var old in _visible.Values)
        {
            if (_nextVisible.GetValueOrDefault(old.Cycle.ModuleId) != old)
            {
                context.Flight.StructureBeamEffect(old, false);
            }
        }
        foreach (var beam in _nextVisible.Values)
        {
            if (_visible.GetValueOrDefault(beam.Cycle.ModuleId) != beam)
            {
                context.Flight.StructureBeamEffect(beam, true);
            }
        }
        (_visible, _nextVisible) = (_nextVisible, _visible);
    }

    private void Started(PilotCallContext context, StructureControlToken token, StructureWeaponActivation result)
    {
        if (result.Launch is null && result.Neutralization is null && result.Web is null)
        {
            return;
        }
        Notify(context, token, result.Cycle, true);
        _observed[result.Cycle.ModuleId] = result.Cycle;
        if (result.Launch is { } launch)
        {
            MagazineWire.NotifyState(context, launch.Magazine, result.Cycle.StartedAt);
            context.Flight.RefreshMissiles();
        }
        if (result.Cycle.ReactivationAt > 0)
        {
            context.Notifications.Add(
                new ModuleAttributesChanged([
                    new(
                        token.PilotId,
                        new DogmaItemKey.Item(result.Cycle.ModuleId),
                        DogmaAttributeId.NextActivationTime,
                        result.Cycle.StartedAt,
                        result.Cycle.ReactivationAt
                    ),
                ])
            );
        }
        if (result.Neutralization is { } drain)
        {
            _neutralizers[result.Cycle.ModuleId] = drain.Cycle;
            capacitor.Set(drain.Cycle, true);
        }
        if ((result.Web?.Capacitor ?? result.Neutralization?.Capacitor) is double charge)
        {
            _sourceSystem = context.Pilot.SystemId;
            var beam = new StructureBeam(token.StructureId, result.Cycle);
            _beams[result.Cycle.ModuleId] = beam;
            beams.Set(_sourceSystem, beam, true);
            context.Flight.StructureBeamEffect(beam, true);
            context.Notifications.Add(
                new ModuleAttributesChanged([
                    new(
                        token.PilotId,
                        new DogmaItemKey.Item(token.StructureId),
                        DogmaAttributeId.CapacitorCharge,
                        result.Cycle.StartedAt,
                        charge
                    ),
                ])
            );
        }
    }

    internal PilotCallResult Call(
        PilotCallContext context,
        string method,
        DogmaEffect effect,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        Advance(context);
        if (context.StructureControl?.Token is not { } token)
        {
            return new PilotCallResult.Failure("Take control of a structure before operating its defensive weapons.");
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected structure weapon arguments.");
        }
        try
        {
            if (method == "Activate" && args is [long module, _, long target, long repeats] && repeats is >= 0 and <= int.MaxValue)
            {
                var result = weapons.Activate(token, module, target, (int)repeats, context.Flight.CurrentStamp, effect);
                Started(context, token, result);
                return PilotCallResult.None;
            }
            if (method == "Deactivate" && args is [long stopped, _])
            {
                weapons.Deactivate(token, stopped, effect);
                return PilotCallResult.None;
            }
            return new PilotCallResult.Failure("Choose a locked target and valid structure weapon repeat count.");
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    public void Dispose()
    {
        foreach (var cycle in _neutralizers.Values)
        {
            capacitor.Set(cycle, false);
        }
        foreach (var beam in _beams.Values)
        {
            beams.Set(_sourceSystem, beam, false);
        }
        _beams.Clear();
        _neutralizers.Clear();
    }
}
