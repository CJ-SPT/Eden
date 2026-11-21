using Eden.Data.Catalog.FactionWarfare;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime.Economy;

namespace Eden.World.FactionWarfare;

internal sealed partial class MilitiaComplexStore
{
    private static readonly IGrouping<MilitiaComplexFamily, MilitiaComplexVariant>[] BattlefieldVariants = new[]
    {
        MilitiaComplexVariants.Battlefield,
    }
        .GroupBy(variant => variant.Family)
        .ToArray();

    private static IEnumerable<NpcPopulationMember> BattlefieldWings(WorldAction world, long siteId) =>
        NpcPopulationMember.AtAnchor(world, siteId).Where(npc => npc.AnchorKind == NpcAnchorKind.MilitiaComplex);

    private void AdvanceBattlefield(
        WorldAction world,
        CatalogDbContext catalog,
        Site site,
        IReadOnlyList<MilitiaComplexPilot> pilots,
        long now,
        Dictionary<long, Sample?> samples
    )
    {
        var layout = site.Profile.Battlefield!;
        var state = world.Set<SavedMilitiaComplex>().Find(site.Slot);
        if (site.Replaced && state is not null)
        {
            RetireBattlefield(world, catalog, site.Slot, state.Generation, now);
            state.Generation = site.Generation;
            state.Occupier = site.System.Occupier;
            state.Progress = 0;
            state.ReplaceAt = null;
        }

        var wings = BattlefieldWings(world, site.Id).ToArray();
        List<Participant> present = [];
        int earned = 0;
        for (int index = 0; index < layout.Points.Count; index++)
        {
            long id = MilitiaComplexRules.PointId(site.Id, index);
            var previous = _samples.GetValueOrDefault(id);
            if (previous?.At > now)
            {
                continue;
            }
            var position = site.Room + layout.Points[index].Position;
            var pilotsAtPoint = Participants(world, catalog, site, pilots, position);
            present.AddRange(pilotsAtPoint);
            var attackers = pilotsAtPoint.Where(p => p.Side == 1).ToArray();
            var defenders = pilotsAtPoint.Where(p => p.Side == -1).ToArray();
            var activeWing = wings.Where(npc => npc.Slot == index).Select(npc => (NpcPopulationMember?)npc).MaxBy(npc => npc?.Id);
            bool blocked = HoldBattlefieldWing(world, catalog, site, position, index, activeWing, attackers, now);
            var capturing =
                attackers.Length > 0 && defenders.Length == 0 && !blocked ? attackers
                : defenders.Length > 0 && attackers.Length == 0 ? defenders
                : [];
            var ships = capturing.Where(p => p.CanCapture).Select(p => p.Presence).ToArray();
            if (ships.Length == 0)
            {
                samples[id] = null;
                continue;
            }
            int side = capturing[0].Side;
            long held = 0;
            if (
                previous is not null
                && previous.Side == side
                && now - previous.At <= MilitiaComplexRules.MaximumSampleGap.Ticks
                && ships.Intersect(previous.Ships).Any()
            )
            {
                held = previous.TicksHeld + now - previous.At;
                earned += side * (int)(held / MilitiaBattlefieldRules.PointInterval.Ticks);
                held %= MilitiaBattlefieldRules.PointInterval.Ticks;
            }
            samples[id] = new(site.Id, now, side, ships, held);
        }

        if (present.Count == 0)
        {
            return;
        }
        state ??= world
            .Add(
                new SavedMilitiaComplex
                {
                    Slot = site.Slot,
                    Occupier = site.System.Occupier,
                    Generation = site.Generation,
                }
            )
            .Entity;
        state.Progress = Math.Clamp(state.Progress + earned, -MilitiaBattlefieldRules.WinningScore, MilitiaBattlefieldRules.WinningScore);
        if (Math.Abs(state.Progress) != MilitiaBattlefieldRules.WinningScore)
        {
            return;
        }

        int winningSide = Math.Sign(state.Progress);
        var winners = present.Where(p => p.Side == winningSide && p.CanCapture).DistinctBy(p => p.Pilot.Id).ToArray();
        Settle(world, catalog, site, winners, winningSide > 0, site.Profile.Variant.Policy, now);
        MilitiaAdvantageStore.Apply(
            world,
            site.System,
            winningSide > 0 ? Opponent(site.System) : site.System.Occupier,
            MilitiaAdvantageRules.BattlefieldContribution,
            now,
            winners.FirstOrDefault()?.Pilot
        );
        RetireBattlefield(world, catalog, site.Slot, site.Generation, now);
        state.ReplaceAt = checked(now + MilitiaBattlefieldRules.Replacement.Ticks);
        for (int index = 0; index < layout.Points.Count; index++)
        {
            samples[MilitiaComplexRules.PointId(site.Id, index)] = null;
        }
    }

    private bool HoldBattlefieldWing(
        WorldAction world,
        CatalogDbContext catalog,
        Site site,
        SpacePosition position,
        int index,
        NpcPopulationMember? last,
        Participant[] attackers,
        long now
    )
    {
        if (last is not { Id: > 0, Active: true })
        {
            if (attackers.Length == 0 || last?.DestroyedAt is long killed && now - killed < site.Profile.DefenderInterval.Ticks)
            {
                return false;
            }

            var npc = SpawnDefender(world, catalog, site, position, index, now);
            last = new(npc.Id, site.Id, npc.TypeId, 0, index, npc.AnchorKind, npc.Role, null, null, default);
        }
        EngageDefender(world, catalog, last.Value.Id, attackers, now);
        return world.Set<WorldNpc>().Find(last.Value.Id) is { } defender
            && position.DistanceTo(new(defender.X, defender.Y, defender.Z)) <= MilitiaComplexRules.CaptureRange;
    }

    private static void RetireBattlefield(WorldAction world, CatalogDbContext catalog, long slot, int generation, long now)
    {
        foreach (var npc in BattlefieldWings(world, MilitiaComplexRules.SiteId(slot, generation)).Where(npc => npc.Active))
        {
            RetireNpc(world, catalog, npc.Id, now);
        }
    }

    private void BattlefieldBodies(Site site, long now, List<StaticSpaceObject> bodies)
    {
        var layout = site.Profile.Battlefield!;
        for (int index = 0; index < layout.Points.Count; index++)
        {
            var point = layout.Points[index];
            long id = MilitiaComplexRules.PointId(site.Id, index);
            var sample = _samples.GetValueOrDefault(id);
            long? next =
                sample is not null && now >= sample.At && now - sample.At <= MilitiaComplexRules.MaximumSampleGap.Ticks
                    ? sample.At + MilitiaBattlefieldRules.PointInterval.Ticks - sample.TicksHeld
                    : null;
            bodies.Add(
                point with
                {
                    Id = id,
                    Position = site.Room + point.Position,
                    BattlefieldCapture = new(Opponent(site.System), site.System.Occupier, next),
                }
            );
        }
        bodies.Add(
            layout.Scoreboard with
            {
                Id = MilitiaComplexRules.PointId(site.Id, layout.Points.Count),
                Position = site.Room + layout.Scoreboard.Position,
                BattlefieldScore = site.Replaced ? 0 : (int)(site.Saved?.Progress ?? 0),
            }
        );
    }
}
