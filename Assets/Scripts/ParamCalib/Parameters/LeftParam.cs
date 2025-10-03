
/// <summary>
/// Calibration parameter for left translation (meters, absolute value).
/// </summary>
public struct LeftParam : ICalibParameter {
    private float _value;
    private float _safety;

    public float Value { get => _value; set => _value = value; }
    public float Safety { get => _safety; set => _safety = value; }
}


