using System.Collections.Immutable;
using System.Text.Json;
using Eden.Data.World.Alliances;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.World.FactionWarfare;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed record LocalPilotIdentity(CharacterIdentity Identity, int CorporationId);

internal sealed record LocalCorporationIdentity(int Id, string SearchName, string DetailsJson)
{
    private readonly Lazy<CorporationDirectoryEntry> _entry = new(() => Read(Id, DetailsJson));
    internal CorporationDirectoryEntry Entry => _entry.Value;

    private static CorporationDirectoryEntry Read(int id, string json)
    {
        var details = JsonSerializer.Deserialize<CorporationDetails>(json)!;
        return new(id, details.Name, details.Ticker, details.Logo);
    }
}

internal sealed record OwnerDirectorySnapshot(
    ImmutableDictionary<long, LocalPilotIdentity> Pilots,
    ImmutableDictionary<int, LocalCorporationIdentity> Corporations,
    Lazy<SearchNameIndex> CharacterSearch,
    Lazy<SearchNameIndex> CorporationSearch,
    ImmutableDictionary<int, int> AllianceMemberships,
    ImmutableDictionary<long, MilitiaFaction> DirectMilitias,
    ImmutableDictionary<int, MilitiaFaction> OrganizationMilitias
)
{
    internal MilitiaFaction? PilotMilitia(long id)
    {
        if (!Pilots.TryGetValue(id, out var pilot))
        {
            return null;
        }
        int corporation = pilot.CorporationId;
        int owner = AllianceMemberships.GetValueOrDefault(corporation, corporation);
        return MilitiaRules.CorporationFaction(corporation)
            ?? (OrganizationMilitias.TryGetValue(owner, out var organization) ? (MilitiaFaction?)organization : null)
            ?? (DirectMilitias.TryGetValue(id, out var direct) ? direct : null);
    }
}

// Published identities contain committed public fields only. Readers never acquire persistence or system ownership.
internal sealed class LiveOwnerDirectory(WorldDbContext world)
{
    private OwnerDirectorySnapshot _snapshot = Load(world);
    internal OwnerDirectorySnapshot Snapshot => Volatile.Read(ref _snapshot);

    private static OwnerDirectorySnapshot Load(WorldDbContext world)
    {
        var pilots = world
            .Pilots.AsNoTracking()
            .Select(pilot => new LocalPilotIdentity(new(pilot.Id, pilot.Name, pilot.GenderId), pilot.CorporationId))
            .ToImmutableDictionary(pilot => pilot.Identity.Id);
        var corporations = world
            .Set<SavedCorporation>()
            .AsNoTracking()
            .Select(corporation => new LocalCorporationIdentity(corporation.Id, corporation.Name, corporation.DetailsJson))
            .ToImmutableDictionary(corporation => corporation.Id);
        var alliances = world
            .Set<SavedAllianceMember>()
            .AsNoTracking()
            .ToImmutableDictionary(row => row.CorporationId, row => row.AllianceId);
        var direct = world
            .Set<SavedMilitiaEnlistment>()
            .AsNoTracking()
            .Where(row => row.DirectFaction.HasValue)
            .ToImmutableDictionary(row => row.PilotId, row => row.DirectFaction!.Value);
        var organizations = world
            .Set<SavedMilitiaOrganization>()
            .AsNoTracking()
            .Where(row => row.Phase == MilitiaOrganizationPhase.Active || row.Phase == MilitiaOrganizationPhase.Leaving)
            .ToImmutableDictionary(row => row.OwnerId, row => row.Faction);
        return new(pilots, corporations, Characters(pilots), CorporationNames(corporations), alliances, direct, organizations);
    }

    private static Lazy<SearchNameIndex> Characters(ImmutableDictionary<long, LocalPilotIdentity> pilots) =>
        new(() => new(pilots.Values.Select(pilot => new SearchEntry(SearchCategory.Character, pilot.Identity.Id, pilot.Identity.Name))));

    private static Lazy<SearchNameIndex> CorporationNames(ImmutableDictionary<int, LocalCorporationIdentity> corporations) =>
        new(() =>
            new(
                corporations.Values.Select(corporation => new SearchEntry(
                    SearchCategory.Corporation,
                    corporation.Id,
                    corporation.SearchName
                ))
            )
        );

    internal void Observe(WorldDbContext world, ResidentEconomy economy, IReadOnlyCollection<CombatRowChange> changes)
    {
        if (changes.Any(change => change.Table == "schema"))
        {
            Volatile.Write(ref _snapshot, Load(world));
            return;
        }
        long[] pilotIds = changes.Where(change => change.Table == "WorldPilot").Select(change => change.Id).Distinct().ToArray();
        long[] corporationIds = changes
            .Where(change => change.Table == "WorldCorporation")
            .Select(change => change.Id)
            .Distinct()
            .ToArray();
        long[] allianceCorporations = changes
            .Where(change => change.Table == "WorldAllianceMember")
            .Select(change => change.Id)
            .Distinct()
            .ToArray();
        long[] directIds = changes
            .Where(change => change.Table == "WorldMilitiaEnlistment")
            .Select(change => change.Id)
            .Distinct()
            .ToArray();
        long[] organizationIds = changes
            .Where(change => change.Table == "WorldMilitiaOrganization")
            .Select(change => change.Id)
            .Distinct()
            .ToArray();
        if (pilotIds.Length + corporationIds.Length + allianceCorporations.Length + directIds.Length + organizationIds.Length == 0)
        {
            return;
        }
        var previous = Snapshot;
        var pilots = previous.Pilots;
        var corporations = previous.Corporations;
        var alliances = previous.AllianceMemberships;
        var pilotRows =
            pilotIds.Length == 0 ? []
            : economy.IsLoaded ? pilotIds.Select(economy.Peek<Pilot>).OfType<Pilot>().ToArray()
            : world.Pilots.AsNoTracking().Where(pilot => pilotIds.Contains(pilot.Id)).ToArray();
        var corporationRows =
            corporationIds.Length == 0 ? []
            : economy.IsLoaded ? corporationIds.Select(economy.Peek<SavedCorporation>).OfType<SavedCorporation>().ToArray()
            : world.Set<SavedCorporation>().AsNoTracking().Where(corporation => corporationIds.Contains(corporation.Id)).ToArray();
        var foundPilots = pilotRows.ToDictionary(pilot => pilot.Id);
        var foundCorporations = corporationRows.ToDictionary(corporation => corporation.Id);
        bool characterNamesChanged = false;
        bool corporationNamesChanged = false;
        foreach (long id in pilotIds)
        {
            var before = pilots.GetValueOrDefault(id);
            LocalPilotIdentity? after = foundPilots.TryGetValue(id, out var row)
                ? new(new(row.Id, row.Name, row.GenderId), row.CorporationId)
                : null;
            if (before == after)
            {
                continue;
            }
            characterNamesChanged |= before?.Identity.Name != after?.Identity.Name;
            pilots = after is null ? pilots.Remove(id) : pilots.SetItem(id, after);
        }
        foreach (long id in corporationIds)
        {
            int key = checked((int)id);
            var before = corporations.GetValueOrDefault(key);
            var row = foundCorporations.GetValueOrDefault(key);
            if (before?.SearchName == row?.Name && before?.DetailsJson == row?.DetailsJson)
            {
                continue;
            }
            LocalCorporationIdentity? after = row is null ? null : new(row.Id, row.Name, row.DetailsJson);
            corporationNamesChanged |= before?.SearchName != after?.SearchName;
            corporations = after is null ? corporations.Remove(key) : corporations.SetItem(key, after);
        }
        foreach (long corporationId in allianceCorporations)
        {
            int key = checked((int)corporationId);
            var row = economy.IsLoaded
                ? economy.Peek<SavedAllianceMember>(corporationId)
                : world.Set<SavedAllianceMember>().AsNoTracking().SingleOrDefault(member => member.CorporationId == key);
            alliances = row is null ? alliances.Remove(key) : alliances.SetItem(key, row.AllianceId);
        }
        var direct = previous.DirectMilitias;
        foreach (long id in directIds)
        {
            direct = economy.Peek<SavedMilitiaEnlistment>(id)?.DirectFaction is { } faction
                ? direct.SetItem(id, faction)
                : direct.Remove(id);
        }
        var organizations = previous.OrganizationMilitias;
        foreach (long id in organizationIds)
        {
            int key = checked((int)id);
            organizations = MilitiaOrganizationStore.ActiveFaction(economy.Peek<SavedMilitiaOrganization>(id)) is { } faction
                ? organizations.SetItem(key, faction)
                : organizations.Remove(key);
        }
        if (
            pilots != previous.Pilots
            || corporations != previous.Corporations
            || alliances != previous.AllianceMemberships
            || direct != previous.DirectMilitias
            || organizations != previous.OrganizationMilitias
        )
        {
            Volatile.Write(
                ref _snapshot,
                new(
                    pilots,
                    corporations,
                    characterNamesChanged ? Characters(pilots) : previous.CharacterSearch,
                    corporationNamesChanged ? CorporationNames(corporations) : previous.CorporationSearch,
                    alliances,
                    direct,
                    organizations
                )
            );
        }
    }
}
