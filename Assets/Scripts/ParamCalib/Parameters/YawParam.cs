 

/// <summary>
/// Calibration parameter for yaw (degrees, absolute value).
/// </summary>
public struct YawParam : ICalibParameter {
    private float _value;
    private float _safety;

    public float Value { get => _value; set => _value = value; }
    public float Safety { get => _safety; set => _safety = value; }

    public override string ToString() => $"Value={_value:F3}°, Safety={_safety:F3}";
}


