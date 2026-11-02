using Eden.Protocol.Docking;

namespace Eden.Protocol.Combat;

public sealed record NpcContact(long Id, int TypeId, int GroupId, int CategoryId, int CorporationId, string Name, int SystemId,
    SpacePosition Position, double Mass, float Radius, float MaxSpeed, float Agility, ShipHealth Health, NpcWeaponContact? Weapon = null);

public interface INpcEncounters
{
    IReadOnlyList<NpcContact> List(int systemId, long now);
    bool WasDestroyed(long npcId);
}

public static class NpcEncounterDefinitions
{
    // Eden's first encounter placement policy, rather than catalog spawn data.
    // Catalog Guristas Rookies wait at Perimeter's belt anchors. Weapon fire and
    // retaliation are integrated separately; merely arriving does not start a fight.
    public const int PerimeterSystem = 30000144;
    public const int GuristasRookieType = 30380;
    public const int GuristasCorporation = 1000127;
    public const long FirstNpcId = 4_000_000_000_000;
    public const double AnchorOffset = 3500;
    public const double VisibilityRange = 250_000;
    public const int RespawnSeconds = 600;
    // This type has no agility override and the catalog default is zero. A
    // stationary free ball uses a positive inertia until NPC movement is modeled.
    public const float StationaryAgility = 1;
}
