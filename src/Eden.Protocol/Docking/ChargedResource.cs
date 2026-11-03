namespace Eden.Protocol.Docking;

public static class ChargedResource
{
    // Build 3662168 godma.CreateChargedAttribute divides the catalog recharge
    // duration by five before evaluating GetChargeValue's exponential curve.
    private const double RechargeTimeScale = 5;

    public static double TimeConstant(double rechargeMilliseconds) => rechargeMilliseconds / RechargeTimeScale;

    public static double Recharged(double charge, double capacity, double rechargeMilliseconds, double elapsedMilliseconds)
    {
        if (capacity <= 0)
        {
            return 0;
        }
        if (rechargeMilliseconds <= 0)
        {
            return capacity;
        }
        double fraction = Math.Clamp(charge / capacity, 0, 1);
        double root = 1 + (Math.Sqrt(fraction) - 1) * Math.Exp(-Math.Max(0, elapsedMilliseconds) / TimeConstant(rechargeMilliseconds));
        return capacity * root * root;
    }
}
