namespace DtsMonitor.App.Models;

public sealed class ShapeSensingProfile
{
    public int Channel { get; init; }
    public float[] SensorPositionsM { get; init; } = Array.Empty<float>();
    public float[] StrainSensitivity { get; init; } = Array.Empty<float>();
    public float[] ReferenceStrainWavelengthsNm { get; init; } = Array.Empty<float>();
}

public sealed class ShapeReconstructionSettings
{
    public int StartIndex { get; set; }
    public int EndIndex { get; set; } = int.MaxValue;
    public int PairOffset { get; set; }
    public ShapeSensingMode Mode { get; set; } = ShapeSensingMode.SingleFiber;
    public float PhotoElasticCoefficient { get; set; } = 0.22f;
    public float GratingDistanceM { get; set; } = 0.003f;
    public float NeutralAxisDistanceM { get; set; } = 0.003f;
    public float FineStepM { get; set; } = 0.005f;
    public int SmoothWindow { get; set; } = 7;
    public bool AutoScale { get; set; } = true;
    public float XAxisMaxM { get; set; } = 20f;
    public float YAxisMaxM { get; set; } = 0.1f;
}

public sealed class ShapeReconstructionResult
{
    public long TimestampMs { get; init; }
    public DateTime Timestamp { get; init; }
    public int Channel { get; init; }
    public bool IsValid { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public float[] ArcPositionsM { get; init; } = Array.Empty<float>();
    public float[] ShapeX { get; init; } = Array.Empty<float>();
    public float[] ShapeY { get; init; } = Array.Empty<float>();
    public float[] SensorArcPositionsM { get; init; } = Array.Empty<float>();
    public float[] Curvature { get; init; } = Array.Empty<float>();
    public float[] StrainTopMicro { get; init; } = Array.Empty<float>();
    public float[] StrainBottomMicro { get; init; } = Array.Empty<float>();
    public float MaxDeflectionM { get; init; }
    public int PairCount { get; init; }
    public ShapeSensingMode Mode { get; init; } = ShapeSensingMode.SingleFiber;
}

public enum ShapeSensingMode
{
    SingleFiber,
    DualFiber
}
