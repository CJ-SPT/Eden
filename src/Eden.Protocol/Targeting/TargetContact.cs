using Eden.Gameplay.Flight;

namespace Eden.Protocol.Targeting;

internal sealed record TargetContact(long Id, int TypeId, SpacePosition Position, float Radius);
