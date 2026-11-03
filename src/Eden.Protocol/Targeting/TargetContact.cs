using Eden.Gameplay.Flight;
using Eden.Protocol.Docking;

namespace Eden.Protocol.Targeting;

internal sealed record TargetContact(long Id, int TypeId, SpacePosition Position, float Radius);
