using System;
using UnityEngine;

public readonly struct AbsoluteRotationData : ITimeSeriesData {
    public DateTime Timestamp { get; }
    public Quaternion Rotation { get; }
    public AbsoluteRotationData(DateTime timestamp, Quaternion rotation) {
        Timestamp = timestamp;
        Rotation = rotation;
    }
}


