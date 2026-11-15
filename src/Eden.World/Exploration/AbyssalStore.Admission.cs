using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class AbyssalStore
{
    private static void RequireHull(CatalogDbContext catalog, Pilot pilot, AbyssalMode mode)
    {
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        if (!CatalogTypeLists.Read(catalog, AbyssalContent.ShipList(mode)).Contains(hull.Id, hull.GroupId, hull.CategoryId))
        {
            throw new InvalidDataException("Your ship is not permitted in this Abyssal game mode.");
        }
    }

    private void RequireEntryState(WorldAction world, CatalogDbContext catalog, Pilot pilot, int tier, int system)
    {
        world.RequireUncloaked(pilot.ActiveShipId);
        CrimewatchPersistence.CheckTravel(world, catalog, pilot, CrimewatchTravel.Jump, Now, system);
        var timers = world.Set<SavedCrimewatch>().Find(pilot.Id)?.Timers() ?? CrimewatchTimers.Empty;
        if (timers.Pvp.Running(Now))
        {
            throw new InvalidDataException("The capsuleer combat timer prevents Abyssal entry.");
        }
        double security = Math.Round(CrimewatchPersistence.Security(catalog, system), 1);
        if (tier > 0 && security >= 0.9)
        {
            throw new InvalidDataException("Only Tranquil filaments can be used above 0.8 security.");
        }
        if (security >= 0.5 && tier >= 4 && tier + (int)Math.Round(security * 10) >= 12)
        {
            if (pilot.SafetyLevel == ShipSafetyLevel.Full)
            {
                throw new InvalidDataException("Lower your safety before activating a filament that creates a suspect flag.");
            }
            var state = CrimewatchPersistence.State(world, pilot.Id);
            state.SystemId = system;
            state.Apply(state.Timers() with { Suspect = CrimewatchRules.Extend(timers.Suspect, Now, CrimewatchRules.FlagDuration) });
        }
    }

    private void EnterTrace(
        WorldAction world,
        CatalogDbContext catalog,
        SavedAbyssalRun run,
        SavedAbyssalParticipant participant,
        Pilot pilot,
        long gate
    )
    {
        var position = pilot.ActiveShip.Position!;
        if (
            gate != AbyssalRules.Trace(run.Id)
            || run.Mode == (int)AbyssalMode.Cruiser
            || run.Status >= (int)AbyssalStatus.Completed
            || Now >= run.ExpiresAt
            || run.Room != 0
            || CrimewatchPersistence.System(catalog, pilot.ActiveShip) != run.OriginSystemId
            || new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(run.OriginX, run.OriginY, run.OriginZ))
                > AbyssalRules.ActivationRange
        )
        {
            throw new InvalidDataException("This Abyssal entrance is unavailable or out of range.");
        }
        RequireHull(catalog, pilot, (AbyssalMode)run.Mode);
        RequireEntryState(world, catalog, pilot, run.Tier, run.OriginSystemId);
        if (
            world.Filter<SavedAbyssalParticipant>(p => p.RunId == run.Id && p.Entered).Count()
            >= AbyssalContent.Capacity((AbyssalMode)run.Mode)
        )
        {
            throw new InvalidDataException("This Abyssal entrance has reached its pilot limit.");
        }
        if (run.Status == (int)AbyssalStatus.Deployed)
        {
            run.Status = (int)AbyssalStatus.Active;
            run.ExpiresAt = checked(Now + AbyssalRules.Duration);
            run.EnvironmentAt = Now;
            Spawn(world, catalog, run, Now);
        }
        participant.Entered = true;
        participant.ShipId = pilot.ActiveShipId;
        participant.Room = 0;
        participant.OutsideSince = 0;
        SqliteWorld.SetPosition(world, catalog, pilot.ActiveShipId, AbyssalRules.Center(0), run.SystemId, Now);
    }

    private static void CloseInvitations(WorldAction world, SavedAbyssalRun run)
    {
        foreach (var invitation in world.Filter<SavedAbyssalParticipant>(p => p.RunId == run.Id && !p.Entered))
        {
            invitation.Finished = true;
        }
    }
}
