namespace Eden.Protocol.Tests.Administration;

internal sealed class PinnedSkillClientFactAttribute : FactAttribute
{
    public PinnedSkillClientFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")))
        {
            Skip = "Set EDEN_TEST_CLIENT_ROOT to the pinned source client for headless package integration.";
        }
    }
}
