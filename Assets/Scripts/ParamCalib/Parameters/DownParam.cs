 

/// <summary>
/// Calibration parameter for down translation (meters, absolute value).
/// </summary>
public struct DownParam : ICalibParameter {
    private float _value;
    private float _safety;

    public float Value { get => _value; set => _value = value; }
    public float Safety { get => _safety; set => _safety = value; }

    public override string ToString() => $"Value={_value:F3}m, Safety={_safety:F3}";
}


