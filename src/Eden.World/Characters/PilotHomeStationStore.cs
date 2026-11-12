using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotHomeStationStore(WorldDatabase database, SqliteWorld lookup, TimeProvider clock, PilotTrainingStore training)
    : IPilotHomeStation
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static Pilot Pilot(WorldDbContext world, long id) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == id)
        ?? throw new InvalidDataException("Unknown pilot.");

    private static CatalogStationDefinition? Station(CatalogDbContext catalog, int id) => catalog.FindStation(id);

    private static HomeStation Snapshot(CatalogStationDefinition station, bool fallback = false) =>
        new(station.Id, station.TypeId, station.SolarSystemId, fallback);

    private static int? SchoolHeadquarters(CatalogDbContext catalog, Pilot pilot)
    {
        long? corporation = catalog.Read(CatalogData.Client.Schools, (long)pilot.SchoolId)?.CorporationID;
        long? station = corporation is long id ? catalog.Read(CatalogData.Client.Npccorporations, (long)id)?.StationID : null;
        return station is > 0 and <= int.MaxValue ? (int)station : null;
    }

    internal static HomeStation Read(WorldDbContext world, CatalogDbContext catalog, Pilot pilot) =>
        Read(new SqlWorldAction(world), catalog, pilot);

    internal static HomeStation Read(WorldAction world, CatalogDbContext catalog, Pilot pilot)
    {
        int id = world.Set<SavedHomeStation>().Find(pilot.Id)?.StationId ?? pilot.ActiveShip.StationId;
        if (Station(catalog, id) is { } saved)
        {
            return Snapshot(saved);
        }
        int fallback = SchoolHeadquarters(catalog, pilot) ?? pilot.ActiveShip.StationId;
        return Snapshot(Station(catalog, fallback) ?? throw new InvalidDataException("No valid home station is available."), true);
    }

    public HomeStation Read(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Read(world, catalog, Pilot(world, pilotId));
    }

    public IReadOnlyList<HomeStationCandidate> Candidates(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Candidates(world, catalog, Pilot(world, pilotId));
    }

    private static IReadOnlyList<HomeStationCandidate> Candidates(WorldDbContext world, CatalogDbContext catalog, Pilot pilot)
    {
        var home = Read(world, catalog, pilot);
        int? current = pilot.ActiveShip.Position is null && pilot.ActiveShip.StructureId is null ? pilot.ActiveShip.StationId : null;
        int? school = SchoolHeadquarters(catalog, pilot);
        // Eden's pilots currently belong to NPC corporations; there are no
        // player-corporation offices that grant additional remote candidates.
        return new[] { current, school }
            .OfType<int>()
            .Distinct()
            .Select(id => Station(catalog, id))
            .OfType<CatalogStationDefinition>()
            .OrderBy(station => station.Id)
            .Select(station => new HomeStationCandidate(
                Snapshot(station),
                station.Id == current,
                station.Id == school,
                HomeStationRules.Errors(station.Id, home.Id, station.SolarSystemId, station.RegionId)
            ))
            .ToArray();
    }

    public long? NextRemoteChange(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        _ = Pilot(world, pilotId);
        // Current station and school HQ changes never consume the remote-office
        // cooldown. NPC-corporation pilots have no remote-office entitlement.
        return null;
    }

    public HomeStationChange Set(long pilotId, int stationId, bool allowRemote)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var candidate =
            Candidates(world, catalog, pilot).SingleOrDefault(candidate => candidate.Station.Id == stationId)
            ?? throw new InvalidDataException("Choose your current station or your school's headquarters.");
        if (candidate.Errors.Count > 0)
        {
            throw new InvalidDataException(
                candidate.Errors.Contains(HomeStationError.AlreadyHome)
                    ? "That station is already your home."
                    : "That station cannot be selected as your home."
            );
        }
        // allowRemote is confirmation, never permission to select an arbitrary
        // station. All currently eligible candidates are exempt from cooldown.
        _ = allowRemote;
        var wallet = world.Set<PilotWallet>().Find(pilotId);
        long balance = HomeStationRules.Pay(wallet?.Cents ?? 0);
        wallet!.Cents = balance;
        var saved = world.Set<SavedHomeStation>().Find(pilotId) ?? world.Add(new SavedHomeStation { PilotId = pilotId }).Entity;
        saved.StationId = stationId;
        world.Add(
            new SavedHomeStationPayment
            {
                PilotId = pilotId,
                StationId = stationId,
                OperatorId = Station(catalog, stationId)!.OwnerId,
                AmountCents = HomeStationRules.RelocationFeeCents,
                BalanceCents = balance,
                Time = Now,
            }
        );
        world.SaveChanges();
        transaction.Commit();
        return new(candidate.Station, balance);
    }

    public HomeCloneTransfer SelfDestruct(long pilotId, long expectedShipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var old = pilot.ActiveShip;
        var home = Read(world, catalog, pilot);
        if (old.Id != expectedShipId || old.OwnerId != pilotId)
        {
            throw new InvalidDataException("Your active capsule changed before the transfer.");
        }
        var type = catalog.FittingType(old.TypeId) ?? throw new InvalidDataException("Unknown active capsule.");
        HomeStationRules.CheckSelfDestruct(
            old.Position is null,
            type.GroupId == FleetDefinitions.CapsuleGroup,
            old.DockedLocationId == home.Id
        );
        var capsuleType =
            (
                catalog.FittingType(FleetDefinitions.CapsuleType) is { GroupId: FleetDefinitions.CapsuleGroup } replacement
                    ? replacement
                    : null
            ) ?? throw new InvalidDataException("The catalog has no replacement capsule.");
        var previous = PilotFleetStore.Read(catalog, old);
        var capsule = world
            .Add(
                new Ship
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilotId,
                    TypeId = capsuleType.Id,
                    Name = capsuleType.Name,
                    StationId = home.Id,
                }
            )
            .Entity;
        long now = Now;
        world.Add(
            new SavedHomeCloneTransfer
            {
                PilotId = pilotId,
                OldShipId = old.Id,
                NewShipId = capsule.Id,
                SourceStationId = old.DockedLocationId,
                DestinationStationId = home.Id,
                Time = now,
            }
        );
        PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.PodLost);
        training.ChangeAttributes(world, pilotId, () => PilotJumpCloneStore.ResetActive(world, pilotId));
        pilot.ActiveShip = capsule;
        pilot.ActiveShipId = capsule.Id;
        world.SaveChanges();
        ShipStoragePersistence.Destroy(world, old);
        world.SaveChanges();
        var incoming = PilotFleetStore.Read(catalog, capsule);
        transaction.Commit();
        return new(lookup.FindPilot(pilot.AccountId, pilotId)!, [new(previous with { Quantity = 0 }, previous), new(incoming, null)]);
    }
}

[Injectable]
internal sealed class NpcCorporationDirectory(WorldDatabase database) : INpcCorporationDirectory
{
    public NpcCorporationInfo Read(int id)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (catalog.NpcCorporationName(id) is null)
        {
            throw new InvalidDataException("Unknown NPC corporation.");
        }
        return Read(catalog, id);
    }

    public IReadOnlyList<NpcCorporationInfo> Read(IReadOnlyList<int> ids)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return ids.Distinct().Where(id => catalog.NpcCorporationName(id) is not null).Order().Select(id => Read(catalog, id)).ToArray();
    }

    private static NpcCorporationInfo Read(CatalogDbContext catalog, int id) => catalog.Definition(s_info, id);

    private static readonly CatalogDefinition<NpcCorporationInfo> s_info = new("directory/npc-corporation", Load);

    private static NpcCorporationInfo Load(CatalogDbContext catalog, int id)
    {
        var data =
            catalog.Read(CatalogData.Client.Npccorporations, (long)id)
            ?? throw new InvalidDataException("Missing NPC corporation metadata.");
        return new(
            id,
            catalog.NpcCorporationName(id)!,
            data.TickerName ?? "",
            checked((int?)data.StationID),
            checked((int?)data.SolarSystemID),
            data.FactionID,
            data.CeoID,
            data.TaxRate ?? 0,
            data.Shares ?? 0,
            data.MemberLimit ?? 0,
            data.Url ?? ""
        );
    }
}
