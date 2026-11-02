using Eden.Protocol.Mining;

namespace Eden.Protocol.Targeting;

public enum TargetingSkill
{
    LongRangeTargeting = 3428,
    TargetManagement = 3429,
    AdvancedTargetManagement = 3430,
    SignatureAnalysis = 3431,
}

public static class TargetingDefinitions
{
    public const int BasePilotTargetLimit = 2;
    public const int AsteroidCategory = AsteroidDefinitions.Category;

    // CCP's useful-formulae guide: seconds = 40000 / (scan resolution * asinh(signature radius)^2).
    private const double LockTimeScale = 40_000;

    public static double LockSeconds(double scanResolution, double signatureRadius) =>
        LockTimeScale / (scanResolution * Math.Pow(Math.Asinh(signatureRadius), 2));
}
