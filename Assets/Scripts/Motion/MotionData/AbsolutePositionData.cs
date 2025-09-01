using System;
using UnityEngine;

public readonly struct AbsolutePositionData : ITimeSeriesData {
    public DateTime Timestamp { get; }
    public Vector3 Position { get; }

    public AbsolutePositionData(DateTime timestamp, Vector3 position){
        Timestamp = timestamp;
        Position = position;
    }
}


