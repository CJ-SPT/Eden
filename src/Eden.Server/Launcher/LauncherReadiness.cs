using Eden.Server.Administration;
using Eden.Server.Authentication;
using Eden.World.Modding;

namespace Eden.Server.Launcher;

internal sealed record LauncherWorld(
    LauncherStore Accounts,
    DevelopmentIdentity Identity,
    string? ChatCertificatePem,
    SkillClientPublisher? SkillPublisher,
    ModStore? Mods
);

internal sealed record LauncherAvailability(string State, string? SetupUrl = null, LauncherWorld? World = null);

internal sealed class LauncherReadiness
{
    internal const string SetupRequired = "setup_required";
    internal const string Starting = "starting";
    internal const string Ready = "ready";
    private LauncherAvailability _availability = new(Starting);

    public LauncherAvailability Read() => Volatile.Read(ref _availability);

    public void RequireSetup(string? setupUrl) => Volatile.Write(ref _availability, new(SetupRequired, setupUrl));

    public void StartWorld() => Volatile.Write(ref _availability, new(Starting));

    public void Open(LauncherWorld world) => Volatile.Write(ref _availability, new(Ready, World: world));

    public LauncherWorld RequireWorld()
    {
        var availability = Read();
        return availability.World
            ?? throw new LauncherException(
                availability.State == SetupRequired ? "world_setup_required" : "world_starting",
                availability.State == SetupRequired
                    ? "An administrator must complete world setup before you can sign in."
                    : "The world is starting. Check again shortly.",
                503
            );
    }
}
