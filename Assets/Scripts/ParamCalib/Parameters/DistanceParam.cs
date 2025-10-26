public struct DistanceParam : ICalibParameter {
    public string Id { get; set; }
    public float Value { get; set; }
    public float Safety { get; set; }

    public override string ToString() => $"Id={Id}, Value={Value:F3}, Safety={Safety:F3}";
}


