namespace Eden.Protocol.Tests.PilotSessions;

internal sealed class PinnedPortraitClientFactAttribute : FactAttribute
{
    public PinnedPortraitClientFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")))
        {
            Skip = "Set EDEN_TEST_CLIENT_ROOT to the pinned source client for headless portrait adapter checks.";
        }
    }
}
