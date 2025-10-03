 

/// <summary>
/// Calibration parameter for roll (degrees, absolute value).
/// </summary>
public struct RollParam : ICalibParameter {
    private float _value;
    private float _safety;

    public float Value { get => _value; set => _value = value; }
    public float Safety { get => _safety; set => _safety = value; }
}


