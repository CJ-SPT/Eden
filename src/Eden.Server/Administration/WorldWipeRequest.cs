namespace Eden.Server.Administration;

internal enum WorldRecreation
{
    Reuse,
    Configure,
}

internal sealed record WorldWipePlan(string BackupDirectory, WorldRecreation Recreation, long? ConfigurationGeneration);

internal sealed class WorldWipeRequest(string worldPath)
{
    internal const string Confirmation = "WIPE WORLD";
    private readonly object _gate = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorldWipePlan? _plan;

    internal Task ShutdownRequested => _shutdown.Task;
    internal WorldWipePlan? Pending => Volatile.Read(ref _plan);

    internal WorldWipePlan Prepare(
        string confirmation,
        WorldRecreation recreation = WorldRecreation.Reuse,
        long? configurationGeneration = null
    )
    {
        if (!string.Equals(confirmation, Confirmation, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Type {Confirmation} exactly to confirm deletion.");
        }
        lock (_gate)
        {
            if (_plan is not null)
            {
                throw new InvalidOperationException("A world wipe has already been requested.");
            }
            string backup = Path.Combine(
                Path.GetFullPath(worldPath) + ".backups",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
                    + "-"
                    + Guid.NewGuid().ToString("N")
            );
            _plan = new(backup, recreation, configurationGeneration);
            return _plan;
        }
    }

    internal Task BeginShutdown()
    {
        lock (_gate)
        {
            if (_plan is null)
            {
                throw new InvalidOperationException("Prepare a world wipe before requesting shutdown.");
            }
            _shutdown.TrySetResult();
        }
        return Task.CompletedTask;
    }
}
