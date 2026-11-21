using Eden.Gameplay.Corporations;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerCorporationRules
{
    public const int FoundingRoster = 6;
    public const int TargetMembers = 12;
    public const double BountyTaxRate = 0.10;
    public const int MaximumIdentityAttempts = 64;

    private static readonly string[] s_modifiers =
    [
        "Amber",
        "Ashen",
        "Braided",
        "Cinder",
        "Copper",
        "Drift",
        "Evening",
        "Fallow",
        "First",
        "Glass",
        "Grey",
        "Hollow",
        "Iron",
        "Juniper",
        "Lantern",
        "Last",
        "Low",
        "Morrow",
        "North",
        "Ochre",
        "Orchard",
        "Quiet",
        "Reed",
        "Second",
        "Sable",
        "Salt",
        "Silver",
        "Still",
        "Tarn",
        "Third",
        "White",
        "Winter",
    ];
    private static readonly string[] s_subjects =
    [
        "Anchor",
        "Bell",
        "Bridge",
        "Canopy",
        "Chorus",
        "Compass",
        "Covenant",
        "Current",
        "Field",
        "Foundry",
        "Harbor",
        "Hearth",
        "Keel",
        "Ledger",
        "Mantle",
        "March",
        "Mast",
        "Meridian",
        "Mill",
        "Needle",
        "Orchard",
        "Quarry",
        "Reef",
        "Relay",
        "Ridge",
        "Shift",
        "Spire",
        "Thread",
        "Traverse",
        "Vale",
        "Wake",
        "Weir",
    ];
    private static readonly string[] s_cooperativeEndings = ["Collective", "Cooperative", "Guild", "Union"];
    private static readonly string[] s_companyEndings = ["& Company", "Partners", "Syndicate", "Works"];
    private static readonly string[] s_legacyBrands =
    [
        "Meridian",
        "Parallax",
        "Waypoint",
        "Horizon",
        "Relay",
        "Vanguard",
        "Pioneer",
        "Arc",
    ];

    // Native corporation palette shapes and colors used by build 3662168.
    private static readonly int[] s_shapes = [415, 416, 417];
    private static readonly int[] s_colors = [671, 672, 673];

    public static CorporationDetails Identity(
        string system,
        long seed,
        long founderId,
        string founderName,
        int attempt,
        bool mining,
        bool frontier
    )
    {
        ulong draw = CapsuleerProvisioningRules.Seed(seed ^ founderId, unchecked((int)(founderId >> 32)), CapsuleerCareer.Hauling, attempt);
        string modifier = Pick(s_modifiers, draw >> 4);
        string subject = Pick(s_subjects, draw >> 15);
        string surname = founderName.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (surname.Length is < 2 or > 24 || !surname.Any(char.IsAsciiLetter) || surname.Any(char.IsControl))
        {
            surname = subject;
        }
        string name = (draw % 10) switch
        {
            0 => modifier + subject.ToLowerInvariant(),
            1 when attempt == 0 => $"{surname} {Pick(s_companyEndings, draw >> 28)}",
            1 or 2 => $"{surname} {subject} {Pick(s_companyEndings, draw >> 28)}",
            3 or 4 => $"{modifier} {subject} {Pick(s_cooperativeEndings, draw >> 28)}",
            5 when frontier => $"The {modifier} {subject}",
            _ => $"{modifier} {subject}",
        };
        string[] words = name.Split(' ');
        string acronym = new(words.Where(word => char.IsAsciiLetter(word[0])).Select(word => word[0]).ToArray());
        string letters = new(name.Where(char.IsAsciiLetterOrDigit).ToArray());
        int prefixLength = attempt == 0 ? 5 : 3;
        string ticker = string.Concat(acronym, letters).ToUpperInvariant();
        ticker = ticker[..Math.Min(prefixLength, ticker.Length)];
        if (attempt > 0)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            ticker += $"{alphabet[(int)((draw >> 36) % 36)]}{alphabet[(int)((draw >> 46) % 36)]}";
        }
        return CorporationRules.Validate(
            new(name, ticker, Description(system, mining, frontier), "", BountyTaxRate, 0, Logo(draw >> 54), true, false)
        );
    }

    private static string Pick(string[] choices, ulong draw) => choices[draw % (ulong)choices.Length];

    private static CorporationLogo Logo(ulong draw) =>
        new(
            s_shapes[draw % (ulong)s_shapes.Length],
            null,
            null,
            s_colors[(draw / (ulong)s_shapes.Length) % (ulong)s_colors.Length],
            null,
            null,
            null
        );

    private static string Description(string system, bool mining, bool frontier) =>
        mining ? $"Local miners and freight pilots based in {system}."
        : frontier ? $"Frontier freight routes serving {system}."
        : $"Station and gate traffic serving {system}.";

    public static CorporationDetails LegacyIdentity(string system, long seed, int systemId, int attempt, bool mining, bool frontier)
    {
        ulong draw = CapsuleerProvisioningRules.Seed(seed, systemId, CapsuleerCareer.Hauling, attempt);
        string business =
            mining ? "Mining and Freight"
            : frontier ? "Frontier Freight"
            : "Transit";
        string brand = Pick(s_legacyBrands, draw);
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
        return new(name, ticker, Description(system, mining, frontier), "", BountyTaxRate, 0, Logo(draw), true, false);
    }
}
