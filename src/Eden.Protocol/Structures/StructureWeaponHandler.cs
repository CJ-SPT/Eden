using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable(InjectionType.Scoped)]
internal sealed class StructureWeaponHandler(IPilotStructureWeapons weapons, TimeProvider clock)
{
    private StructureControlToken? _token;
    private readonly Dictionary<long, StructureWeaponCycle> _observed = [];

    internal static DogmaActiveEffect Effect(StructureControlToken token, StructureWeaponCycle cycle) =>
        new(
            cycle.ModuleId,
            token.PilotId,
            token.StructureId,
            cycle.TargetId,
            DogmaEffect.UseMissiles,
            cycle.StartedAt,
            cycle.DurationMilliseconds,
            cycle.Repeats
        );

    private void Notify(PilotCallContext context, StructureControlToken token, StructureWeaponCycle cycle, bool active)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
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

    private void Started(PilotCallContext context, StructureControlToken token, StructureWeaponActivation result)
    {
        if (result.Launch is not { } launch)
        {
            return;
        }
        Notify(context, token, result.Cycle, true);
        _observed[result.Cycle.ModuleId] = result.Cycle;
        MagazineWire.NotifyState(context, launch.Magazine, result.Cycle.StartedAt);
        context.Flight.RefreshMissiles();
    }

    internal PilotCallResult Call(PilotCallContext context, string method, object?[] args, Dictionary<string, object?>? keywords)
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
                var result = weapons.Activate(token, module, target, (int)repeats, context.Flight.CurrentStamp);
                Started(context, token, result);
                return PilotCallResult.None;
            }
            if (method == "Deactivate" && args is [long stopped, _])
            {
                weapons.Deactivate(token, stopped);
                return PilotCallResult.None;
            }
            return new PilotCallResult.Failure("Choose a locked target and valid structure weapon repeat count.");
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
