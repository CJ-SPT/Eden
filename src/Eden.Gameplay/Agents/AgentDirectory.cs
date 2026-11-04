using Eden.Gameplay.Characters;

namespace Eden.Gameplay.Agents;

public static class NpcCharacterIds
{
    // Build 3662168 inventorycommon.const minAgent/maxAgent, consumed by
    // idCheckers.IsNPCCharacter. Player IDs must not initialize the NPC catalog.
    public const long First = 3_000_000;
    public const long Last = 3_999_999;

    public static bool Contains(long id) => id is >= First and <= Last;
}

// Build 3662168 client/agenttypes.
public enum AgentType
{
    NonAgent = 1,
    Basic = 2,
    Tutorial = 3,
    Research = 4,
    Concord = 5,
    GenericStoryline = 6,
    Storyline = 7,
    Event = 8,
    FactionalWarfare = 9,
    EpicArc = 10,
    Aura = 11,
    Career = 12,
    Heraldry = 13,
}

public sealed record CatalogAgent(
    long Id,
    int? StationId,
    int? SystemId,
    int CorporationId,
    AgentType Type,
    int? FactionId,
    int DivisionId,
    int Level,
    bool Locator
);

public sealed record NpcCharacter(
    CharacterIdentity Identity,
    int CorporationId,
    int? RaceId,
    int? BloodlineId,
    int? SchoolId,
    int? AncestryId,
    long? CreatedAt,
    string Description
);

public interface INpcDirectory
{
    IReadOnlyList<CatalogAgent> Agents();
    CatalogAgent? Agent(long id);
    NpcCharacter? Character(long id);
    IReadOnlyList<NpcCharacter> Characters(IReadOnlyList<long> ids);
}
