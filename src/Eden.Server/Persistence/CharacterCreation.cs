using System.Globalization;
using Eden.Protocol;
using Eden.Protocol.Characters;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class CharacterCreation(WorldDatabase database, TimeProvider clock) : ICharacterCreation
{
    public int ValidateName(string name)
    {
        using WorldDbContext world = new(database.Path);
        return ValidateName(world, name);
    }

    private static int ValidateName(WorldDbContext world, string name)
    {
        // Build 3662168 maps these codes to localized messages in ChooseNameSection.
        if (name.Length < 3) { return -1; }
        if (name.Length > 37) { return -2; }
        if (name.Trim() != name || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (' ' or '\'' or '-'))) { return -5; }
        if (name.Count(c => c == ' ') > 2) { return -6; }
        if (name.Contains("  ", StringComparison.Ordinal)) { return -7; }
        return world.Pilots.Any(p => EF.Functions.Collate(p.Name, "NOCASE") == name) ? -101 : 1;
    }

    public long Create(long accountId, NewCharacter character)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        if (ValidateName(world, character.Name) != 1 || world.Pilots.Count(p => p.AccountId == accountId) >= ICharacterCreation.Slots)
        {
            throw new InvalidDataException("Name unavailable or all character slots occupied.");
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
        var school = Record<ClientSchools>("schools", character.SchoolId);
        var race = Record<ClientRaces>("races", character.RaceId);
        var bloodline = Record<ClientBloodlines>("bloodlines", character.BloodlineId);
        if (school?.RaceID != character.RaceId || bloodline?.RaceID != character.RaceId ||
            race?.ShipTypeID is not long hull || character.GenderId is not (0 or 1) ||
            school.CorporationID is not long corp || school.StartingStations is not { Length: > 0 } stations ||
            (character.AncestryId != 64 && Record<ClientAncestries>("ancestries", character.AncestryId)?.BloodlineID != character.BloodlineId))
        {
            throw new InvalidDataException("Invalid character origin.");
        }
        var candidates = catalog.Set<Station>().Where(s => stations.Contains(s.Id));
        if (school.IsStarterSpaceSchool != 1 || stations.Length != 1)
        {
            var systems = CatalogData.Client.Schoolmap.Query(catalog).AsEnumerable()
                .Where(e => e.Value.SchoolID == character.SchoolId).Select(e => e.Value.SolarSystemID).Distinct().ToArray();
            if (systems is not [long system])
            {
                throw new InvalidDataException("Missing or ambiguous school system.");
            }
            candidates = candidates.Where(s => s.SolarSystemId == system);
        }
        if (candidates.Select(s => s.Id).ToArray() is not [int station] ||
            !catalog.Set<NpcCorporation>().Any(c => c.Id == corp))
        {
            throw new InvalidDataException("Missing or ambiguous school station or corporation.");
        }
        string shipName = catalog.Set<ItemType>().Single(t => t.Id == hull).Name;
        long pilotId = (world.Pilots.Max(p => (long?)p.Id) ?? 90000000) + 1;
        long shipId = (world.Ships.Max(s => (long?)s.Id) ?? 1000000000000) + 1;
        long created = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        // The pilot and active ship reference each other. Check both links at commit.
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
        world.Database.ExecuteSql($"""
            INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId,CreatedAt,RaceId,BloodlineId,
                GenderId,AncestryId,SchoolId,Appearance,Portrait)
            VALUES ({pilotId},{accountId},{character.Name},{corp},{shipId},{created},{character.RaceId},{character.BloodlineId},
                {character.GenderId},{character.AncestryId},{character.SchoolId},{character.Appearance},{character.Portrait})
            """);
        world.Database.ExecuteSql($"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES ({shipId},{pilotId},{hull},{station},{shipName})");
        WorldMigration.GrantStartingSkills(world, catalog, pilotId, character.RaceId);
        PilotInventoryStore.GrantStarterEquipment(world, pilotId, station);
        transaction.Commit();
        return pilotId;

        T? Record<T>(string source, int id) where T : class =>
            catalog.Set<SourceRecord>().Find("client/" + source, id.ToString(CultureInfo.InvariantCulture))?.Read<T>();
    }

    public byte[]? ReadAppearance(long characterId, bool portrait)
    {
        using WorldDbContext world = new(database.Path);
        return world.Pilots.Where(p => p.Id == characterId).Select(p => portrait ? p.Portrait : p.Appearance).Single();
    }
}
