using Eden.Gameplay.Corporations;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerCorporationRules
{
    public const int FoundingRoster = 6;
    public const int TargetMembers = 12;
    public const double BountyTaxRate = 0.10;

    private static readonly string[] s_names = ["Meridian", "Parallax", "Waypoint", "Horizon", "Relay", "Vanguard", "Pioneer", "Arc"];

    // Native corporation palette shapes and colors used by build 3662168.
    private static readonly int[] s_shapes = [415, 416, 417];
    private static readonly int[] s_colors = [671, 672, 673];

    public static CorporationDetails Identity(string system, long seed, int systemId, int attempt, bool mining, bool frontier)
    {
        ulong draw = CapsuleerProvisioningRules.Seed(seed, systemId, CapsuleerCareer.Hauling, attempt);
        string business =
            mining ? "Mining and Freight"
            : frontier ? "Frontier Freight"
            : "Transit";
        string brand = s_names[draw % (ulong)s_names.Length];
        string tradeName = brand + " " + business;
        int available = CorporationRules.MaximumNameLength - tradeName.Length - 1;
        string name = system[..Math.Min(system.Length, available)] + " " + tradeName;
        if (attempt > 0)
        {
            string suffix = " " + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            name = name[..Math.Min(name.Length, CorporationRules.MaximumNameLength - suffix.Length)] + suffix;
        }
        // Make the tag recognizable in Local. The last two characters distinguish businesses with the same initials.
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        char location = system.FirstOrDefault(char.IsAsciiLetterOrDigit, 'E');
        string ticker = $"{char.ToUpperInvariant(location)}{brand[0]}{business[0]}{alphabet[(int)(draw % (ulong)alphabet.Length)]}";
        draw /= (ulong)alphabet.Length;
        ticker += alphabet[(int)(draw % (ulong)alphabet.Length)];
        var logo = new CorporationLogo(
            s_shapes[draw % (ulong)s_shapes.Length],
            null,
            null,
            s_colors[(draw / (ulong)s_shapes.Length) % (ulong)s_colors.Length],
            null,
            null,
            null
        );
        string description =
            mining ? $"Local miners and freight pilots based in {system}."
            : frontier ? $"Frontier freight routes serving {system}."
            : $"Station and gate traffic serving {system}.";
        return new(name, ticker, description, "", BountyTaxRate, 0, logo, true, false);
    }
}
