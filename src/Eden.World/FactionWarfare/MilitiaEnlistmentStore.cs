using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Registration;
using Eden.World.Bookmarks;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.FactionWarfare;

[Injectable]
internal sealed class MilitiaEnlistmentStore(WorldDatabase database, TimeProvider clock) : IMilitiaEnlistment
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Militia enrollment requires the resident world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public MilitiaAffiliation Read(long pilotId) => Resident.Economy.Inspect(world => Affiliation(world, RequirePilot(world, pilotId)));

    internal static MilitiaAffiliation Affiliation(WorldAction world, Pilot pilot)
    {
        var saved = world.Set<SavedMilitiaEnlistment>().Find(pilot.Id);
        return new(saved?.DirectFaction, MilitiaRules.CorporationFaction(pilot.CorporationId), saved?.DirectCooldownUntil ?? 0);
    }

    internal static MilitiaFaction? Effective(WorldAction world, Pilot pilot) =>
        world is EconomyTransaction
            ? MilitiaRules.CorporationFaction(pilot.CorporationId) ?? world.Set<SavedMilitiaEnlistment>().Find(pilot.Id)?.DirectFaction
            : null;

    internal static void RequireDocking(WorldAction world, Pilot pilot, int systemId, int shipGroupId)
    {
        if (
            Effective(world, pilot) is { } faction
            && !MilitiaRules.CanUseDocking(
                FactionWarzoneStore.System(world, systemId),
                faction,
                shipGroupId == Eden.Gameplay.Docking.FleetDefinitions.CapsuleGroup
            )
        )
        {
            throw new InvalidDataException("Only capsules can dock or undock in this enemy-occupied warzone system.");
        }
    }

    public IReadOnlyList<MilitiaFaction> AllowedFactions(int corporationId) =>
        Resident.Economy.Inspect(world => Allowed(world.Set<SavedCorporation>().Find(corporationId)));

    public IReadOnlyList<MilitiaFaction> SetAllowedFactions(long pilotId, IReadOnlyList<MilitiaFaction> factions)
    {
        if (factions.Count > 4 || factions.Any(faction => !Enum.IsDefined(faction)) || factions.Distinct().Count() != factions.Count)
        {
            throw new InvalidDataException("Choose distinct empire militias.");
        }
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        var pilot = RequirePilot(world, pilotId);
        var corporation = PlayerCorporation(world, pilot) ?? throw new InvalidDataException("Choose a player corporation.");
        if (corporation.CeoId != pilot.Id && !CorporationRoleStore.Has(world, pilot.Id, corporation, CorporationRole.Director))
        {
            throw new InvalidDataException("Only the CEO or a director may set enlistment policy.");
        }
        var allowed = factions.Order().ToArray();
        corporation.DetailsJson = JsonSerializer.Serialize(corporation.Snapshot(0).Details with { AllowedMilitiaFactions = allowed });
        world.Commit();
        return allowed;
    }

    public void EnlistDirectly(long pilotId, MilitiaFaction faction)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        long now = Now;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = RequirePilot(world, pilotId);
        CheckJoin(world, catalog, pilot, faction);
        if (PlayerCorporation(world, pilot) is not { } corporation || !Allowed(corporation).Contains(faction))
        {
            throw new InvalidDataException("Your player corporation must permit direct enlistment with this militia.");
        }
        var saved = Enlistment(world, pilotId);
        if (saved.DirectCooldownUntil > now)
        {
            throw new InvalidDataException("Your direct enlistment cooldown is still active.");
        }
        saved.DirectFaction = faction;
        saved.DirectCooldownUntil = 0;
        world.Commit();
    }

    public void JoinNpcMilitia(long pilotId, MilitiaFaction faction)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        long now = Now;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = RequirePilot(world, pilotId);
        CheckJoin(world, catalog, pilot, faction);
        int militia = MilitiaRules.Corporation(faction);
        if (catalog.NpcCorporationName(militia) is null)
        {
            throw new InvalidDataException("The selected militia corporation is unavailable.");
        }
        if (PlayerCorporation(world, pilot) is not null)
        {
            var departure = CorporationMembershipStore.Permission(world, pilotId, pilotId, now);
            if (!departure.Allowed)
            {
                throw new InvalidDataException(departure.Reason);
            }
        }
        Move(world, pilot, militia, now);
        world.Commit();
        database.NotifyAccessGroupsChanged();
    }

    public void RetireDirectly(long pilotId)
    {
        using var world = Resident.Economy.Begin(TransactionParticipants.None);
        _ = RequirePilot(world, pilotId);
        var saved = world.Set<SavedMilitiaEnlistment>().Find(pilotId);
        if (saved?.DirectFaction is null)
        {
            throw new InvalidDataException("You are not directly enlisted.");
        }
        saved.DirectFaction = null;
        saved.DirectCooldownUntil = checked(Now + MilitiaRules.DirectCooldown.Ticks);
        world.Commit();
    }

    internal static void LeaveNpcMilitia(WorldAction world, CatalogDbContext catalog, Pilot pilot, long now)
    {
        int destination = CorporationMembershipStore.OriginalCorporation(world, catalog, pilot.Id);
        if (MilitiaRules.CorporationFaction(destination) is not null)
        {
            throw new InvalidDataException("Your original NPC corporation is unavailable.");
        }
        Move(world, pilot, destination, now);
    }

    private static void Move(WorldAction world, Pilot pilot, int corporationId, long now)
    {
        CorporationMembershipStore.Move(world, pilot, corporationId, now);
        CorporationMembershipStore.WithdrawOtherApplications(world, pilot.Id, null, now);
        world.SaveChanges();
        PilotBookmarksStore.PruneUnavailableConnections(world, pilot.Id);
    }

    private static void CheckJoin(WorldAction world, CatalogDbContext catalog, Pilot pilot, MilitiaFaction faction)
    {
        if (!Enum.IsDefined(faction))
        {
            throw new InvalidDataException("Choose one of the four empire militias.");
        }
        if (Effective(world, pilot) is not null)
        {
            throw new InvalidDataException("Retire from your current militia before enlisting.");
        }
        var ship = pilot.ActiveShip;
        if (ship.Position is not null || ship.StructureId is not null)
        {
            throw new InvalidDataException("Dock at an NPC station belonging to the requested empire.");
        }
        var station = catalog.RequireStation(ship.StationId);
        long? stationFaction =
            catalog.Read(CatalogData.Client.Npccorporations, (long)station.OwnerId)?.FactionID
            ?? catalog.Read(CatalogData.Sde.NpcCorporations, (long)station.OwnerId)?.FactionID;
        if (stationFaction != (int)faction)
        {
            throw new InvalidDataException("Dock at an NPC station belonging to the requested empire.");
        }
        double standing = world.Set<SavedNpcStanding>().Find(pilot.Id, (long)faction)?.Value ?? 0;
        if (standing < 0)
        {
            throw new InvalidDataException("Your unmodified standing with this empire must be nonnegative.");
        }
    }

    private static Pilot RequirePilot(WorldAction world, long pilotId) =>
        world.Pilots.Find(pilotId) is { } pilot && world.Set<SavedCharacterDeletion>().Find(pilotId)?.DeletedAt is null
            ? pilot
            : throw new InvalidDataException("Choose a pilot.");

    private static SavedCorporation? PlayerCorporation(WorldAction world, Pilot pilot) =>
        world.Set<SavedCorporation>().Find(pilot.CorporationId) is { ClosedAt: null } corporation ? corporation : null;

    private static IReadOnlyList<MilitiaFaction> Allowed(SavedCorporation? corporation) =>
        corporation is { ClosedAt: null } ? corporation.Snapshot(0).Details.AllowedMilitiaFactions ?? [] : [];

    private static SavedMilitiaEnlistment Enlistment(WorldAction world, long pilotId) =>
        world.Set<SavedMilitiaEnlistment>().Find(pilotId) ?? world.Add(new SavedMilitiaEnlistment { PilotId = pilotId }).Entity;
}
