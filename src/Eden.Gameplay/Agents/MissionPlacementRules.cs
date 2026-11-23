using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Agents;

public static class MissionPlacementRules
{
    private const int SlotsPerAxis = 1024;
    private const long SlotCount = (long)SlotsPerAxis * SlotsPerAxis * SlotsPerAxis;
    private const double SlotSpacing = 8_000_000;
    private const double MinimumXOffset = 5_500_000_000;
    private const double HalfWidth = SlotsPerAxis / 2 * SlotSpacing;

    public static SpacePosition Place(SpacePosition anchor, long sequence)
    {
        // Cycle through a fixed volume within 0.1 AU of the anchor. Advance across Y and Z first
        // to separate nearby missions along different axes from their room offsets, which run along X.
        long remaining = Math.DivRem(sequence % SlotCount, SlotsPerAxis, out long y);
        long x = Math.DivRem(remaining, SlotsPerAxis, out long z);
        return anchor + new SpacePosition(MinimumXOffset + x * SlotSpacing, y * SlotSpacing - HalfWidth, z * SlotSpacing - HalfWidth);
    }
}
