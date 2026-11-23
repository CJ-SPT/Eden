namespace Eden.Infrastructure.Diagnostics;

/// <summary>Prevents the installed game's synchronous operations from entering a storage adapter.</summary>
public static class GameplaySqlGuard
{
    private static readonly AsyncLocal<bool> s_active = new();

    public static bool IsActive => s_active.Value;

    public static IDisposable Begin() => new Scope();

    public static void Check(string storage)
    {
        if (IsActive)
        {
            throw new InvalidOperationException($"Gameplay attempted to use SQLite ({storage}).");
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly bool _previous = IsActive;

        public Scope() => s_active.Value = true;

        public void Dispose() => s_active.Value = _previous;
    }
}
