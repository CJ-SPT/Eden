using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    public bool VisitCareerSite(long pilotId, int system, long missionId) =>
        LiveWorld.Execute(database.Path, system, () => CompleteCareerVisit(pilotId, system, missionId), nameof(VisitCareerSite));

    private bool CompleteCareerVisit(long pilotId, int system, long missionId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return false;
        }
        var pending = Sites(system)
            .Where(s => s.Id == missionId && s.PilotId == pilotId && (s.CareerVisit is not null || s.CareerHazard is not null))
            .ToArray();
        if (pending.Length == 0)
        {
            return false;
        }
        long now = Now;
        using var world = live.Economy.Begin(new(pilots: [pilotId], systems: [system]));
        if (
            world.Pilots.Find(pilotId) is not { ActiveShip: { Position: { } saved } ship } pilot
            || world.Set<SavedMission>().Find(missionId) is not { State: MissionState.Accepted, ObjectiveComplete: false } mission
            || mission.PilotId != pilotId
            || mission.SystemId != system
            || mission.ExpiresAt <= now
        )
        {
            return false;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        if ((saved.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId) != system)
        {
            return false;
        }
        var position = live.CombatForSystem(system)?.InformationPosition(ship.Id) ?? new SpacePosition(saved.X, saved.Y, saved.Z);
        double shipRadius = catalog.RequireItem(ship.TypeId).Radius ?? 0;
        var site = Array.Find(
            pending,
            s => s.CareerVisit?.Contains(position, shipRadius) == true || s.CareerHazard?.Contains(position) == true
        );
        if (site is null)
        {
            return false;
        }

        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        bool areaChanged =
            site.Objects.FirstOrDefault(body => body.Id == (site.CareerVisit ?? site.CareerHazard)!.ObjectId) is { } target
            && ApplyCareerAreaObjective(world, catalog, pilot, mission, profile, site, target, position, now);
        if (profile.Definition.CareerSacrifice || site.CareerVisit?.Contains(position, shipRadius) != true)
        {
            if (areaChanged)
            {
                world.Commit();
            }
            return areaChanged;
        }
        SpawnTriggeredWaves(world, catalog, mission, profile, MissionWaveTrigger.Visit, 0, now);
        mission.ObjectiveComplete = true;
        world.Commit();
        return true;
    }
}
