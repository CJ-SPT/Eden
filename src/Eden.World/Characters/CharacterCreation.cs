using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class CharacterCreation(WorldDatabase database, TimeProvider clock) : ICharacterCreation
{
    public CharacterNameResult ValidateName(string name)
    {
        using WorldDbContext world = new(database.Path);
        return ValidateName(world, name);
    }

    private static CharacterNameResult ValidateName(WorldDbContext world, string name) =>
        CharacterRules.ValidateName(name, world.Pilots.Any(p => EF.Functions.Collate(p.Name, "NOCASE") == name));

    public long Create(long accountId, NewCharacter character)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        if (
            ValidateName(world, character.Name) != CharacterNameResult.Available
            || world.Pilots.Count(p =>
                p.AccountId == accountId && !world.Set<SavedCharacterDeletion>().Any(d => d.PilotId == p.Id && d.DeletedAt != null)
            ) >= ICharacterCreation.Slots
        )
        {
            throw new InvalidDataException("Name unavailable or all character slots occupied.");
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
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
        long pilotId = (world.Pilots.Max(p => (long?)p.Id) ?? 90000000) + 1;
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
        Eden.World.Capsuleers.CapsuleerCommunityStore.Provision(world, catalog, clock);
        transaction.Commit();
        return pilotId;
    }

    public byte[]? ReadAppearance(long characterId, bool portrait)
    {
        using WorldDbContext world = new(database.Path);
        return world.Pilots.Where(p => p.Id == characterId).Select(p => portrait ? p.Portrait : p.Appearance).Single();
    }
}
