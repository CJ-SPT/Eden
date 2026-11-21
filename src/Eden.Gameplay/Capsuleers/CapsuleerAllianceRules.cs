using System.Globalization;
using Eden.Gameplay.Alliances;

namespace Eden.Gameplay.Capsuleers;

public interface ICapsuleerAlliancePlanning
{
    void Advance();
}

public static class CapsuleerAllianceRules
{
    public const int FoundingCorporations = 2;
    public const int TargetCorporations = 3;
    public const int FirstClaimMaximumJumps = 3;
    public const int FirstHubOrderDays = 7;

    // Build 3662168 cargo and module profiles for the first claim's standard outfitting plan.
    public const int ExpeditionSquall = 81008;
    public const int ExpeditionLink = 34593;
    public const int ExpeditionHaulingSkillLevel = 5;

    public static AllianceDetails Identity(string system, long seed, int systemId, int attempt)
    {
        string suffix = attempt == 0 ? " Compact" : " Compact " + (attempt + 1).ToString(CultureInfo.InvariantCulture);
        string name = system[..Math.Min(system.Length, AllianceRules.MaximumNameLength - suffix.Length)] + suffix;
        ulong draw = CapsuleerProvisioningRules.Seed(seed, systemId, CapsuleerCareer.Hauling, attempt);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        Span<char> ticker = stackalloc char[AllianceRules.MaximumTickerLength];
        ticker[0] = 'A';
        for (int index = 1; index < ticker.Length; index++)
        {
            ticker[index] = alphabet[(int)(draw % (ulong)alphabet.Length)];
            draw /= (ulong)alphabet.Length;
        }
        return new(name, new(ticker), $"A regional alliance of capsuleer corporations based around {system}.", "");
    }
}
