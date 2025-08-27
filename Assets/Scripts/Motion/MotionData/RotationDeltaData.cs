using System;
using UnityEngine;

public readonly struct RotationDeltaData : ITimeSeriesData {
    public DateTime Timestamp { get; }
    public Quaternion Delta { get; }
    public RotationDeltaData(DateTime timestamp, Quaternion delta) {
        Timestamp = timestamp;
        Delta = delta;
    }
}


