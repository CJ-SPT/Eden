using System.Text.Json;
using System.Text.Json.Serialization;
using Eden.Gameplay.Combat;

namespace Eden.Data.Combat;

internal sealed record MissilePayload(MissileFlight Flight, MissileWarhead Warhead);

[JsonSerializable(typeof(MissilePayload))]
[JsonSerializable(typeof(MissileImpact))]
[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true)]
internal sealed partial class MissileJson : JsonSerializerContext;
