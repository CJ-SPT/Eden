namespace Eden.Gameplay.Combat;

public sealed record NpcPirateGroup(int GroupId, NpcPirateFaction Faction, NpcHullClass Hull);

public static class NpcPirateGroups
{
    // Build 3662168 category-11 ordinary asteroid NPC groups. Officer,
    // commander, hauler and capital groups are intentionally not admitted.
    public static IReadOnlyList<NpcPirateGroup> All { get; } =
        Array.AsReadOnly<NpcPirateGroup>([
            new(550, NpcPirateFaction.Angels, NpcHullClass.Frigate),
            new(575, NpcPirateFaction.Angels, NpcHullClass.Destroyer),
            new(551, NpcPirateFaction.Angels, NpcHullClass.Cruiser),
            new(576, NpcPirateFaction.Angels, NpcHullClass.Battlecruiser),
            new(552, NpcPirateFaction.Angels, NpcHullClass.Battleship),
            new(557, NpcPirateFaction.BloodRaiders, NpcHullClass.Frigate),
            new(577, NpcPirateFaction.BloodRaiders, NpcHullClass.Destroyer),
            new(555, NpcPirateFaction.BloodRaiders, NpcHullClass.Cruiser),
            new(578, NpcPirateFaction.BloodRaiders, NpcHullClass.Battlecruiser),
            new(556, NpcPirateFaction.BloodRaiders, NpcHullClass.Battleship),
            new(562, NpcPirateFaction.Guristas, NpcHullClass.Frigate),
            new(579, NpcPirateFaction.Guristas, NpcHullClass.Destroyer),
            new(561, NpcPirateFaction.Guristas, NpcHullClass.Cruiser),
            new(580, NpcPirateFaction.Guristas, NpcHullClass.Battlecruiser),
            new(560, NpcPirateFaction.Guristas, NpcHullClass.Battleship),
            new(567, NpcPirateFaction.Sansha, NpcHullClass.Frigate),
            new(581, NpcPirateFaction.Sansha, NpcHullClass.Destroyer),
            new(566, NpcPirateFaction.Sansha, NpcHullClass.Cruiser),
            new(582, NpcPirateFaction.Sansha, NpcHullClass.Battlecruiser),
            new(565, NpcPirateFaction.Sansha, NpcHullClass.Battleship),
            new(572, NpcPirateFaction.Serpentis, NpcHullClass.Frigate),
            new(583, NpcPirateFaction.Serpentis, NpcHullClass.Destroyer),
            new(571, NpcPirateFaction.Serpentis, NpcHullClass.Cruiser),
            new(584, NpcPirateFaction.Serpentis, NpcHullClass.Battlecruiser),
            new(570, NpcPirateFaction.Serpentis, NpcHullClass.Battleship),
            new(759, NpcPirateFaction.RogueDrones, NpcHullClass.Frigate),
            new(758, NpcPirateFaction.RogueDrones, NpcHullClass.Destroyer),
            new(757, NpcPirateFaction.RogueDrones, NpcHullClass.Cruiser),
            new(755, NpcPirateFaction.RogueDrones, NpcHullClass.Battlecruiser),
            new(756, NpcPirateFaction.RogueDrones, NpcHullClass.Battleship),
        ]);
}
