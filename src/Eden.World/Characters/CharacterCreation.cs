using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Content;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class CharacterCreation(WorldDatabase database, TimeProvider clock) : ICharacterCreation
{
    public CharacterNameResult ValidateName(string name) => WorldActionScope.Inspect(database, world => ValidateName(world, name));

    private static CharacterNameResult ValidateName(WorldAction world, string name) =>
        CharacterRules.ValidateName(name, world.HasPilotName(name));

    public long Create(long accountId, NewCharacter character)
    {
        if (LiveWorld.Find(database.Path) is { } live)
        {
            using var action = live.Economy.Begin(TransactionParticipants.None);
            using CatalogDbContext catalog = new(database.CatalogPath);
            var origin = ReadOrigin(action, catalog, accountId, character);
            long created = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            long pilotId = NextPilotId(action);
            long shipId = action.NextItemId();
            action.Add(new InventoryOwner { Id = pilotId });
            action.Add(
                new Ship
                {
                    Id = shipId,
                    OwnerId = pilotId,
                    TypeId = origin.Hull,
                    StationId = origin.Station,
                    Name = origin.ShipName,
                }
            );
            action.Add(
                new Pilot
                {
                    Id = pilotId,
                    AccountId = accountId,
                    Name = character.Name,
                    CorporationId = origin.Corporation,
                    ActiveShipId = shipId,
                    CreatedAt = created,
                    RaceId = character.RaceId,
                    BloodlineId = character.BloodlineId,
                    GenderId = character.GenderId,
                    AncestryId = character.AncestryId,
                    SchoolId = character.SchoolId,
                    Appearance = character.Appearance,
                    Portrait = character.Portrait,
                }
            );
            if (action.Settings.Progression.GrantStartingSkills)
            {
                StartingSkills.Grant(action, catalog, pilotId, character.RaceId);
            }
            var equipment = BuildContentPolicy.Current.StartingEquipment;
            foreach (int typeId in new[] { equipment.MinerType, equipment.WeaponType })
            {
                action.Add(
                    new OwnedItem
                    {
                        Id = action.NextItemId(),
                        OwnerId = pilotId,
                        TypeId = typeId,
                        LocationId = origin.Station,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 1,
                    }
                );
            }
            TestingWalletFunding.Grant(action, pilotId, created);
            action.Add(new SavedHomeStation { PilotId = pilotId, StationId = origin.Station });
            CapsuleerPopulationStore.Reconcile(action, catalog);
            CapsuleerPopulationStore.ScheduleFounders(action);
            action.Commit();
            return pilotId;
        }
        return CreateFile(accountId, character);
    }

    private static long NextPilotId(WorldAction world) => checked((world.Pilots.Select(p => (long?)p.Id).Max() ?? 90000000) + 1);

    private sealed record CharacterOrigin(int Corporation, int Hull, int Station, string ShipName);

    private static CharacterOrigin ReadOrigin(WorldAction world, CatalogDbContext catalog, long accountId, NewCharacter character)
    {
        var pilotIds = world.Filter<Pilot>(pilot => pilot.AccountId == accountId).Select(pilot => pilot.Id).ToArray();
        if (
            ValidateName(world, character.Name) != CharacterNameResult.Available
            || pilotIds.Count(id => world.Set<SavedCharacterDeletion>().Find(id)?.DeletedAt is null) >= ICharacterCreation.Slots
        )
        {
            throw new InvalidDataException("Name unavailable or all character slots occupied.");
        }

        var school = catalog.Read(CatalogData.Client.Schools, (long)character.SchoolId);
        var race = catalog.Read(CatalogData.Client.Races, (long)character.RaceId);
        var bloodline = catalog.Read(CatalogData.Client.Bloodlines, (long)character.BloodlineId);
        var stations = school?.StartingStations ?? [];
        long hull = race?.ShipTypeID ?? 0;
        long corp = school?.CorporationID ?? 0;
        CharacterRules.ValidateOrigin(
            character,
            new(
                checked((int)(school?.RaceID ?? 0)),
                checked((int)(bloodline?.RaceID ?? 0)),
                hull,
                corp,
                stations,
                character.AncestryId == CharacterRules.UnspecifiedAncestry
                    ? null
                    : catalog.Read(CatalogData.Client.Ancestries, (long)character.AncestryId)?.BloodlineID
            )
        );
        var candidates = catalog.Stations().Where(s => stations.Contains(s.Id));
        if (school!.IsStarterSpaceSchool != 1 || stations.Length != 1)
        {
            var systems = catalog
                .Records(CatalogData.Client.Schoolmap)
                .Where(e => e.Value.SchoolID == character.SchoolId)
                .Select(e => e.Value.SolarSystemID)
                .Distinct()
                .ToArray();
            if (systems is not [long system])
            {
                throw new InvalidDataException("Missing or ambiguous school system.");
            }
            candidates = candidates.Where(s => s.SolarSystemId == system);
        }
        if (candidates.Select(s => s.Id).ToArray() is not [int station] || catalog.NpcCorporationName(checked((int)corp)) is null)
        {
            throw new InvalidDataException("Missing or ambiguous school station or corporation.");
        }
        string shipName = catalog.FittingType(checked((int)hull))?.Name ?? throw new InvalidDataException("Unknown starter hull.");
        return new(checked((int)corp), checked((int)hull), station, shipName);
    }

    private long CreateFile(long accountId, NewCharacter character)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (corp, hull, station, shipName) = ReadOrigin(new SqlWorldAction(world), catalog, accountId, character);
        long pilotId = checked((world.Pilots.Max(p => (long?)p.Id) ?? 90000000) + 1);
        long shipId = InventoryPersistence.NextId(world);
        long created = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        // The pilot and active ship reference each other. Check both links at commit.
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId,CreatedAt,RaceId,BloodlineId,
                GenderId,AncestryId,SchoolId,Appearance,Portrait)
            VALUES ({pilotId},{accountId},{character.Name},{corp},{shipId},{created},{character.RaceId},{character.BloodlineId},
                {character.GenderId},{character.AncestryId},{character.SchoolId},{character.Appearance},{character.Portrait})
            """
        );
        world.Database.ExecuteSql(
            $"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES ({shipId},{pilotId},{hull},{station},{shipName})"
        );
        if (database.Settings.Progression.GrantStartingSkills)
        {
            StartingSkills.Grant(world, catalog, pilotId, character.RaceId);
        }
        PilotInventoryStore.GrantStarterEquipment(world, pilotId, station);
        TestingWalletFunding.Grant(world, pilotId, created);
        world.Add(new SavedHomeStation { PilotId = pilotId, StationId = station });
        world.SaveChanges();

        CapsuleerPopulationStore.Reconcile(world, catalog);
        CapsuleerPopulationStore.ScheduleFounders(world);
        transaction.Commit();
        return pilotId;
    }

    public byte[]? ReadAppearance(long characterId, bool portrait) =>
        WorldActionScope.Inspect(
            database,
            world => world.Filter<Pilot>(p => p.Id == characterId).Select(p => portrait ? p.Portrait : p.Appearance).Single()
        );
}
