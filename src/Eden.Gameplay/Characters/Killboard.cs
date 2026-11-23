namespace Eden.Gameplay.Characters;

public enum KillboardPartyKind
{
    Player,
    Bot,
    Npc,
}

public readonly record struct KillboardCursor(long Time, long Id);

public sealed record KillboardFilter(KillboardPartyKind? Killer, KillboardPartyKind? Victim, KillboardCursor? Before = null);

public sealed record KillboardParty(KillboardPartyKind Kind, string Name, string? Ship);

public sealed record KillboardEntry(KillboardCursor Cursor, string System, KillboardParty Victim, KillboardParty Killer);

public sealed record KillboardPage(IReadOnlyList<KillboardEntry> Entries, KillboardCursor? Older);

public interface IServerKillboard
{
    KillboardPage? Read(KillboardFilter filter);
}
