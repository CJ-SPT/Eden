namespace Eden.Gameplay.Information;

public enum SalvageFamily
{
    Amarr,
    Minmatar,
    Caldari,
    Gallente,
    RogueDrone,
    BloodRaider,
    Sansha,
}

public enum SalvageMaterial
{
    ScorchedTelemetryProcessor = 25588,
    MalfunctioningShieldEmitter = 25589,
    ContaminatedNaniteCompound = 25590,
    ContaminatedLorentzFluid = 25591,
    DefectiveCurrentPump = 25592,
    SmashedTriggerUnit = 25593,
    TangledPowerConduit = 25594,
    AlloyedTritaniumBar = 25595,
    BrokenDroneTransceiver = 25596,
    DamagedArtificialNeuralNetwork = 25597,
    TrippedPowerCircuit = 25598,
    CharredMicroCircuit = 25599,
    BurnedLogicCircuit = 25600,
    FriedInterfaceCircuit = 25601,
    ThrusterConsole = 25602,
    MeltedCapacitorConsole = 25603,
    ConductivePolymer = 25604,
    ArmorPlates = 25605,
    WardConsole = 25606,
    TelemetryProcessor = 25607,
    IntactShieldEmitter = 25608,
    NaniteCompound = 25609,
    LorentzFluid = 25610,
    CurrentPump = 25611,
    TriggerUnit = 25612,
    PowerConduit = 25613,
    SuperalloyIBeam = 25614,
    DroneTransceiver = 25615,
    ArtificialNeuralNetwork = 25616,
    PowerCircuit = 25617,
    MicroCircuit = 25618,
    LogicCircuit = 25619,
    InterfaceCircuit = 25620,
    ImpetusConsole = 25621,
    CapacitorConsole = 25622,
    ConductiveThermoplastic = 25623,
    IntactArmorPlates = 25624,
    EnhancedWardConsole = 25625,
}

public static class SalvageLootRules
{
    // EVE University Salvaging distribution table, reconciled with build 3662168 types.
    // Eden policy: 25% empty; otherwise choose one material uniformly and 1..N units.
    public const int EmptyPercent = 25;
    public const int AccessRollResolution = 1_000_000;
    public const int SmallMaximum = 3;
    public const int MediumMaximum = 6;
    public const int LargeMaximum = 10;

    public static IReadOnlyList<SalvageMaterial> Pool(SalvageFamily family, bool advanced = false) =>
        advanced
            ? family switch
            {
                SalvageFamily.Amarr =>
                [
                    SalvageMaterial.ArtificialNeuralNetwork,
                    SalvageMaterial.CapacitorConsole,
                    SalvageMaterial.CurrentPump,
                    SalvageMaterial.IntactArmorPlates,
                    SalvageMaterial.NaniteCompound,
                    SalvageMaterial.PowerConduit,
                    SalvageMaterial.MicroCircuit,
                    SalvageMaterial.LogicCircuit,
                    SalvageMaterial.InterfaceCircuit,
                    SalvageMaterial.PowerCircuit,
                ],
                SalvageFamily.BloodRaider =>
                [
                    SalvageMaterial.ArtificialNeuralNetwork,
                    SalvageMaterial.CapacitorConsole,
                    SalvageMaterial.PowerConduit,
                    SalvageMaterial.MicroCircuit,
                    SalvageMaterial.LogicCircuit,
                    SalvageMaterial.InterfaceCircuit,
                    SalvageMaterial.PowerCircuit,
                ],
                SalvageFamily.Sansha =>
                [
                    SalvageMaterial.ArtificialNeuralNetwork,
                    SalvageMaterial.CurrentPump,
                    SalvageMaterial.IntactArmorPlates,
                    SalvageMaterial.NaniteCompound,
                    SalvageMaterial.MicroCircuit,
                    SalvageMaterial.LogicCircuit,
                    SalvageMaterial.InterfaceCircuit,
                    SalvageMaterial.PowerCircuit,
                ],
                SalvageFamily.Minmatar =>
                [
                    SalvageMaterial.ArtificialNeuralNetwork,
                    SalvageMaterial.ImpetusConsole,
                    SalvageMaterial.SuperalloyIBeam,
                    SalvageMaterial.TriggerUnit,
                    SalvageMaterial.MicroCircuit,
                    SalvageMaterial.LogicCircuit,
                    SalvageMaterial.InterfaceCircuit,
                    SalvageMaterial.PowerCircuit,
                ],
                SalvageFamily.Caldari =>
                [
                    SalvageMaterial.ArtificialNeuralNetwork,
                    SalvageMaterial.EnhancedWardConsole,
                    SalvageMaterial.IntactShieldEmitter,
                    SalvageMaterial.TelemetryProcessor,
                    SalvageMaterial.MicroCircuit,
                    SalvageMaterial.LogicCircuit,
                    SalvageMaterial.InterfaceCircuit,
                    SalvageMaterial.PowerCircuit,
                ],
                SalvageFamily.Gallente =>
                [
                    SalvageMaterial.ConductiveThermoplastic,
                    SalvageMaterial.DroneTransceiver,
                    SalvageMaterial.LorentzFluid,
                    SalvageMaterial.MicroCircuit,
                    SalvageMaterial.LogicCircuit,
                    SalvageMaterial.InterfaceCircuit,
                    SalvageMaterial.PowerCircuit,
                ],
                _ => [],
            }
            : family switch
            {
                SalvageFamily.Amarr or SalvageFamily.BloodRaider or SalvageFamily.Sansha =>
                [
                    SalvageMaterial.ArmorPlates,
                    SalvageMaterial.CharredMicroCircuit,
                    SalvageMaterial.BurnedLogicCircuit,
                    SalvageMaterial.ContaminatedNaniteCompound,
                    SalvageMaterial.DefectiveCurrentPump,
                    SalvageMaterial.FriedInterfaceCircuit,
                    SalvageMaterial.MeltedCapacitorConsole,
                    SalvageMaterial.TangledPowerConduit,
                    SalvageMaterial.TrippedPowerCircuit,
                ],
                SalvageFamily.Minmatar =>
                [
                    SalvageMaterial.AlloyedTritaniumBar,
                    SalvageMaterial.ArmorPlates,
                    SalvageMaterial.CharredMicroCircuit,
                    SalvageMaterial.BurnedLogicCircuit,
                    SalvageMaterial.ContaminatedNaniteCompound,
                    SalvageMaterial.FriedInterfaceCircuit,
                    SalvageMaterial.MalfunctioningShieldEmitter,
                    SalvageMaterial.SmashedTriggerUnit,
                    SalvageMaterial.ThrusterConsole,
                    SalvageMaterial.TrippedPowerCircuit,
                ],
                SalvageFamily.Caldari =>
                [
                    SalvageMaterial.CharredMicroCircuit,
                    SalvageMaterial.BurnedLogicCircuit,
                    SalvageMaterial.ConductivePolymer,
                    SalvageMaterial.DamagedArtificialNeuralNetwork,
                    SalvageMaterial.FriedInterfaceCircuit,
                    SalvageMaterial.MalfunctioningShieldEmitter,
                    SalvageMaterial.ScorchedTelemetryProcessor,
                    SalvageMaterial.TrippedPowerCircuit,
                    SalvageMaterial.WardConsole,
                ],
                SalvageFamily.Gallente =>
                [
                    SalvageMaterial.ArmorPlates,
                    SalvageMaterial.BrokenDroneTransceiver,
                    SalvageMaterial.CharredMicroCircuit,
                    SalvageMaterial.BurnedLogicCircuit,
                    SalvageMaterial.ConductivePolymer,
                    SalvageMaterial.ContaminatedLorentzFluid,
                    SalvageMaterial.ContaminatedNaniteCompound,
                    SalvageMaterial.DamagedArtificialNeuralNetwork,
                    SalvageMaterial.FriedInterfaceCircuit,
                    SalvageMaterial.TrippedPowerCircuit,
                ],
                SalvageFamily.RogueDrone =>
                [
                    SalvageMaterial.AlloyedTritaniumBar,
                    SalvageMaterial.ArmorPlates,
                    SalvageMaterial.BrokenDroneTransceiver,
                    SalvageMaterial.CharredMicroCircuit,
                    SalvageMaterial.BurnedLogicCircuit,
                    SalvageMaterial.ConductivePolymer,
                    SalvageMaterial.ContaminatedLorentzFluid,
                    SalvageMaterial.ContaminatedNaniteCompound,
                    SalvageMaterial.DefectiveCurrentPump,
                    SalvageMaterial.FriedInterfaceCircuit,
                    SalvageMaterial.MalfunctioningShieldEmitter,
                    SalvageMaterial.MeltedCapacitorConsole,
                    SalvageMaterial.ScorchedTelemetryProcessor,
                    SalvageMaterial.SmashedTriggerUnit,
                    SalvageMaterial.TangledPowerConduit,
                    SalvageMaterial.ThrusterConsole,
                    SalvageMaterial.TrippedPowerCircuit,
                    SalvageMaterial.WardConsole,
                ],
                _ => [],
            };

    public static IReadOnlyList<ScanItem> Roll(SalvageFamily family, bool advanced, double difficulty, uint seed)
    {
        var pool = Pool(family, advanced);
        if (pool.Count == 0)
        {
            throw new InvalidDataException("No verified material pool for this wreck family.");
        }
        if (seed % 100 < EmptyPercent)
        {
            return [];
        }
        int maximum =
            difficulty >= 30 ? SmallMaximum
            : difficulty >= 20 ? MediumMaximum
            : LargeMaximum;
        int material = (int)pool[(int)(seed / 100 % (uint)pool.Count)];
        return [new(material, 1 + (int)(seed / 100 / (uint)pool.Count % (uint)maximum))];
    }
}
