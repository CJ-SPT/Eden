using System.Globalization;
using Eden.Gameplay.Characters;
using Eden.Protocol;
using Eden.Protocol.Characters;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

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
            || world.Pilots.Count(p => p.AccountId == accountId) >= ICharacterCreation.Slots
        )
        {
            throw new InvalidDataException("Name unavailable or all character slots occupied.");
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
        var school = Record<ClientSchools>("schools", character.SchoolId);
        var race = Record<ClientRaces>("races", character.RaceId);
        var bloodline = Record<ClientBloodlines>("bloodlines", character.BloodlineId);
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
                    : Record<ClientAncestries>("ancestries", character.AncestryId)?.BloodlineID
            )
        );
        var candidates = catalog.Set<Station>().Where(s => stations.Contains(s.Id));
        if (school!.IsStarterSpaceSchool != 1 || stations.Length != 1)
        {
            var systems = CatalogData
                .Client.Schoolmap.Query(catalog)
                .AsEnumerable()
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
        if (candidates.Select(s => s.Id).ToArray() is not [int station] || !catalog.Set<NpcCorporation>().Any(c => c.Id == corp))
        {
            throw new InvalidDataException("Missing or ambiguous school station or corporation.");
        }
        string shipName = catalog.Set<ItemType>().Single(t => t.Id == hull).Name;
        long pilotId = (world.Pilots.Max(p => (long?)p.Id) ?? 90000000) + 1;
        long shipId = PilotInventoryStore.NextId(world);
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
        StartingSkills.Grant(world, catalog, pilotId, character.RaceId);
        PilotInventoryStore.GrantStarterEquipment(world, pilotId, station);
        transaction.Commit();
        return pilotId;

        T? Record<T>(string source, int id)
            where T : class => catalog.Set<SourceRecord>().Find("client/" + source, id.ToString(CultureInfo.InvariantCulture))?.Read<T>();
    }

    public byte[]? ReadAppearance(long characterId, bool portrait)
    {
        using WorldDbContext world = new(database.Path);
        return world.Pilots.Where(p => p.Id == characterId).Select(p => portrait ? p.Portrait : p.Appearance).Single();
    }
}
