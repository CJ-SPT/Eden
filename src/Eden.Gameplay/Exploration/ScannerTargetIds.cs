using System.Globalization;

namespace Eden.Gameplay.Exploration;

public static class ScannerTargetIds
{
    private const int AlphabetSize = 26;
    private const int Prefixes = AlphabetSize * AlphabetSize * AlphabetSize;
    private const int Suffixes = 1000;

    // Native scan rows have a narrow ID column. Vary the letters first so
    // adjacent sites remain distinguishable even when its last digit is clipped.
    public static string FromSequence(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (sequence >= Prefixes * Suffixes)
        {
            throw new InvalidDataException("The scanner target identifier space is exhausted.");
        }
        int prefix = (int)(sequence % Prefixes);
        int cycle = (int)(sequence / Prefixes);
        int suffix = cycle % 10 * 100 + cycle / 10 % 10 * 10 + cycle / 100;
        return string.Concat(
            (char)('A' + prefix % AlphabetSize),
            (char)('A' + prefix / AlphabetSize % AlphabetSize),
            (char)('A' + prefix / (AlphabetSize * AlphabetSize)),
            "-",
            suffix.ToString("000", CultureInfo.InvariantCulture)
        );
    }
}
