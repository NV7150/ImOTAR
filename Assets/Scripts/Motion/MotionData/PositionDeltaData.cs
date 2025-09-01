using System;
using UnityEngine;

public readonly struct PositionDeltaData : ITimeSeriesData {
    public DateTime Timestamp { get; }
    public Vector3 Delta { get; }

    public PositionDeltaData(DateTime timestamp, Vector3 delta){
        Timestamp = timestamp;
        Delta = delta;
    }
}


