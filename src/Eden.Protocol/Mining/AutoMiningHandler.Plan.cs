using System.Collections.Immutable;
using Eden.Gameplay.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Mining;

internal sealed partial class AutoMiningHandler
{
    private static bool ValidPlanArguments(object?[] args) =>
        args
            is [
                null
                or string
                or byte[]
                or List<object?>,
                long family,
                long order,
                bool,
                bool,
                long,
                long
                and >= 1
                and <= 100,
                List<object?> systems,
                long mode,
            ]
        && ValidOptions(family, order)
        && mode is >= (long)AutoMiningFleetMode.Solo and <= (long)AutoMiningFleetMode.Follow
        && systems.Count <= AutoMiningRoute.MaximumSystems
        && systems.All(system => system is long and > 0 and <= int.MaxValue);

    private void SavePlan(PilotCallContext context, object?[] args)
    {
        var route = new AutoMiningRoute(
            ((List<object?>)args[7]!).Select(id => (int)(long)id!).ToImmutableArray(),
            (AutoMiningFleetMode)(long)args[8]!
        );
        if (_flight is not null)
        {
            throw new InvalidDataException("Stop AutoMining before saving a new mining plan.");
        }
        var plan = new AutoMiningPlan(
            args[0] is null ? _filter! : ReadFilter(args[0]),
            ReadOptions(args.AsSpan(1, 4)),
            new((long)args[5]!, (int)(long)args[6]!),
            route
        );
        (routes ?? throw new InvalidDataException("AutoMining routes are unavailable.")).SavePlan(_pilotId, context.Pilot.SystemId, plan);
        _filter = plan.Filter;
        _options = plan.Options;
        _haulSettings = plan.Hauling;
        _route = plan.Route;
    }
}

internal sealed record AutoMiningSystems(IReadOnlyList<AutoMiningSystem> Systems) : BlueModel
{
    public override object ToBlue() =>
        new BlueList<AutoMiningSystemRow>(Systems.Select(system => new AutoMiningSystemRow(system)).ToArray());
}

internal sealed record AutoMiningSystemRow(AutoMiningSystem System) : BlueModel
{
    public override object ToBlue() => new object[] { System.Id, new BlueUnicode(System.Name) };
}
