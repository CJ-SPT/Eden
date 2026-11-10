using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

internal sealed record AgencyEssSettings : BlueModel
{
    // Dormant display defaults from the inspected evejs dynamicResourceState.js.
    // Native ESS filters/tooltips require these even when the system list is empty.
    // No setting here participates in Eden's authoritative bounty calculation.
    private const double MinimumOutput = 1;
    private const double MaximumOutput = 2;
    private const double EquilibriumOutput = 1.5;
    private const int OutputIntervalSeconds = 86400;
    private const double BountyCut = 0.4;
    private const double MainBankPayRatio = 0.975;
    private const int AutopaymentIntervalSeconds = 9900;
    private const int BufferSeconds = 360;
    private const int ReservePulseSeconds = 60;
    private const int MaximumReservePayout = 15_000_000;
    private const int ReserveTimeAccessFactor = 3;
    private const double ReserveWindowAccessFactor = 1.5;
    private const int ReservePayoutExponent = 2;
    private const int ReservePayoutPadding = 0;
    private const double EarlyPayoutThreshold = 2.0 / 3;
    private const double LatePayoutThreshold = 1.0 / 3;

    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["minOutput"] = MinimumOutput,
            ["maxOutput"] = MaximumOutput,
            ["equilibriumValue"] = EquilibriumOutput,
            ["timeIntervalSeconds"] = OutputIntervalSeconds,
            ["bountiesCut"] = BountyCut,
            ["mainBankPayRatio"] = MainBankPayRatio,
            ["autopaymentIntervalSeconds"] = AutopaymentIntervalSeconds,
            ["bufferTimeSeconds"] = BufferSeconds,
            ["reservePulseIntervalSeconds"] = ReservePulseSeconds,
            ["reserveBankMaxPayout"] = MaximumReservePayout,
            ["reserveBankTimeAccessFactor"] = ReserveTimeAccessFactor,
            ["reserveWindowAccessFactor"] = ReserveWindowAccessFactor,
            ["reservePayoutExponentLead"] = ReservePayoutExponent,
            ["reservePayoutExponentTail"] = ReservePayoutExponent,
            ["reservePayoutPaddingValue"] = ReservePayoutPadding,
            ["earlyPayoutThresholdFactor"] = EarlyPayoutThreshold,
            ["latePayoutThresholdFactor"] = LatePayoutThreshold,
        };
}
