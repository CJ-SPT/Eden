using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Gameplay.FactionWarfare;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.FactionWarfare;

internal static class MilitiaFriendlyFire
{
    internal static bool Record(WorldAction world, CatalogDbContext catalog, Pilot source, Pilot victim, int system, long now)
    {
        if (
            source.Id == victim.Id
            || MilitiaEnlistmentStore.Effective(world, source) is not { } faction
            || MilitiaEnlistmentStore.Effective(world, victim) != faction
        )
        {
            return false;
        }
        bool inside =
            source.ActiveShip.Position is { } attacker
            && victim.ActiveShip.Position is { } target
            && CrimewatchPersistence.System(catalog, source.ActiveShip) == system
            && CrimewatchPersistence.System(catalog, victim.ActiveShip) == system
            && MilitiaComplexStore.IsSameActiveCaptureGrid(
                world,
                catalog,
                system,
                new(attacker.X, attacker.Y, attacker.Z),
                new(target.X, target.Y, target.Z),
                now
            );
        double multiplier = inside
            ? MilitiaProgressionRules.InsideSiteFriendlyFireMultiplier
            : MilitiaProgressionRules.OutsideSiteFriendlyFireMultiplier;
        var standing =
            world.Set<SavedNpcStanding>().Find(source.Id, (long)faction)
            ?? world.Add(new SavedNpcStanding { PilotId = source.Id, SourceId = (int)faction }).Entity;
        double before = standing.Value;
        MilitiaProgressionStore.Apply(
            world,
            source,
            standing,
            -MilitiaProgressionRules.FriendlyFireBaseFactionLoss * multiplier,
            StandingEvent.CombatAggressionOwnFaction,
            victim.CorporationId,
            system,
            now,
            changes: null,
            victim.ActiveShip.TypeId
        );
        if (standing.Value != before && LiveWorld.Find(world.WorldPath)?.Fleets is { } fleets)
        {
            double after = standing.Value;
            world.AfterCommit(() => fleets.NotifyStandingChanged(source.Id, (int)faction, after));
        }
        return true;
    }
}
