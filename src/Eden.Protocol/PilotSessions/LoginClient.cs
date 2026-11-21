using System.Globalization;
using Eden.Gameplay.Configuration;

namespace Eden.Protocol.PilotSessions;

internal static class LoginClient
{
    private static readonly Lazy<byte[]> s_expression = new(() => Create(new(), 1));

    internal static byte[] For(PlanetaryPolicy policy, decimal trainingSpeedMultiplier = 1) =>
        policy == new PlanetaryPolicy() && trainingSpeedMultiplier == 1 ? Expression : Create(policy, trainingSpeedMultiplier);

    internal static string TrainingPrelude(decimal multiplier) =>
        $"eden_training_speed = {multiplier.ToString(CultureInfo.InvariantCulture)}\n";

    internal static string PlanetaryPrelude(PlanetaryPolicy policy)
    {
        string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
        return $"eden_planetary_policy = ('{Number(policy.CpuMultiplier)}', "
            + $"'{Number(policy.PowergridMultiplier)}', '{Number(policy.FactoryOutputMultiplier)}', "
            + $"'{Number(policy.ExtractionYieldMultiplier)}', '{Number(policy.StorageCapacityMultiplier)}', "
            + $"'{Number(policy.FactorySpeedMultiplier)}')\n"
            + $"eden_planetary_maximum = {policy.MaximumPlanets.ToString(CultureInfo.InvariantCulture)}\n"
            + $"eden_planetary_customs_type = {Eden.Gameplay.Planetary.PlanetaryRules.CustomsOfficeType}\n";
    }

    private static byte[] Create(PlanetaryPolicy policy, decimal trainingSpeedMultiplier) =>
        ClientExpression.CreateWithPrelude(
            "eden-login",
            PlanetaryPrelude(policy) + TrainingPrelude(trainingSpeedMultiplier),
            "Eden.Protocol.Skills.clone_license_client.py",
            "Eden.Protocol.Characters.portrait_client.py",
            "Eden.Protocol.Flight.concord_overview_client.py",
            "Eden.Protocol.Flight.npc_bounty_client.py",
            "Eden.Protocol.Docking.presence_lists_client.py",
            "Eden.Protocol.Corporations.corporation_diagnostics_client.py",
            "Eden.Protocol.Skills.free_points_client.py",
            "Eden.Protocol.Skills.training_speed_client.py",
            "Eden.Protocol.Planetary.planetary_client.py",
            "Eden.Protocol.Planetary.customs_transfer_client.py"
        );

    internal static byte[] Expression => s_expression.Value;
}
