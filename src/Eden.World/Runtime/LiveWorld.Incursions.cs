using System.Collections.Immutable;
using Eden.Gameplay.Exploration;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private sealed record IncursionPublication(
        ImmutableList<IncursionAgencyUpdate> Agency,
        ImmutableDictionary<int, IncursionSystemEffect> Systems
    );

    private IncursionPublication _incursions = new(
        ImmutableList<IncursionAgencyUpdate>.Empty,
        ImmutableDictionary<int, IncursionSystemEffect>.Empty
    );

    internal IReadOnlyList<IncursionAgencyUpdate> IncursionAgencyUpdates => Volatile.Read(ref _incursions).Agency;

    internal IncursionSystemEffect? IncursionEffectForSystem(int systemId, long now)
    {
        var systems = Volatile.Read(ref _incursions).Systems;
        return systems.TryGetValue(systemId, out var effect) && now < effect.EndsAt ? effect : null;
    }

    internal void SetIncursionFocus(int lane, IncursionFocus? focus, long now, IncursionAgencyUpdate? terminal = null)
    {
        // Called under the resident owner. Publish the site index and wire projections in one step.
        var previous = Volatile.Read(ref _incursions);
        var prior = previous.Agency.FirstOrDefault(update => IncursionRules.Lane(update.TaleId) == lane);
        IncursionAgencyUpdate? next = terminal;
        if (focus is not null)
        {
            next = new(focus.TaleId, focus.LastUpdated, focus.Influence, focus.HasFinalEncounter, true, focus.Systems, focus.EndsAt);
        }
        else if (next is null && prior is { IsActive: true })
        {
            next = prior with
            {
                IsActive = false,
                HasFinalEncounter = false,
                LastUpdated = Math.Min(now, IncursionRules.EndsAt(prior.TaleId)),
            };
        }
        if (
            next is null
            || prior == next
            || (
                prior is not null
                && (next.TaleId < prior.TaleId || next.TaleId == prior.TaleId && (!prior.IsActive || next.LastUpdated < prior.LastUpdated))
            )
        )
        {
            return;
        }

        var systems = previous.Systems;
        if (prior is { Systems: { } oldSystems })
        {
            systems = systems.RemoveRange(
                oldSystems
                    .Select(system => system.SystemId)
                    .Where(systemId => systems.TryGetValue(systemId, out var current) && current.TaleId == prior.TaleId)
            );
        }
        if (focus is not null)
        {
            systems = systems.SetItems(
                focus.Systems.Select(system => KeyValuePair.Create(system.SystemId, IncursionSystemEffectRules.Create(focus, system)))
            );
        }
        var agency = previous.Agency.Where(update => IncursionRules.Lane(update.TaleId) != lane).Append(next);
        Volatile.Write(ref _incursions, new(agency.OrderBy(update => update.TaleId).ToImmutableList(), systems));
    }
}
