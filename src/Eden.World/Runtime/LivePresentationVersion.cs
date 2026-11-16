namespace Eden.World.Runtime;

// Shared changes conservatively invalidate every system; local movement and combat
// invalidate only their owning system. Versions are never checkpointed gameplay state.
internal readonly record struct LivePresentationVersion(long Shared, long System);
