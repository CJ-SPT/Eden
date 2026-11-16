namespace Eden.Gameplay.Flight;

public enum RegionalGateStyle
{
    Amarr,
    Caldari,
    Gallente,
    Minmatar,
    Concord,
}

public static class RegionalGateRules
{
    // Build-3662168 group-10 types. Special, disrupted and damaged gates do not define a region's ordinary style.
    public static RegionalGateStyle? Style(int typeId) =>
        typeId switch
        {
            17 or 29624 or 29625 or 29626 => RegionalGateStyle.Amarr,
            16 or 3873 or 29627 or 29629 => RegionalGateStyle.Caldari,
            3874 or 3875 or 3876 or 29632 => RegionalGateStyle.Gallente,
            3877 or 29633 or 29634 or 29635 => RegionalGateStyle.Minmatar,
            93638 or 93639 => RegionalGateStyle.Concord,
            _ => null,
        };

    public static int Type(RegionalGateStyle source, RegionalGateStyle destination) =>
        (source, source != destination) switch
        {
            (RegionalGateStyle.Amarr, false) => 29626,
            (RegionalGateStyle.Amarr, true) => 29625,
            (RegionalGateStyle.Caldari, false) => 29629,
            (RegionalGateStyle.Caldari, true) => 3873,
            (RegionalGateStyle.Gallente, false) => 29632,
            (RegionalGateStyle.Gallente, true) => 3876,
            (RegionalGateStyle.Minmatar, false) => 29635,
            (RegionalGateStyle.Minmatar, true) => 29634,
            // The catalog has no separate CONCORD border model.
            (RegionalGateStyle.Concord, _) => 93639,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
}
