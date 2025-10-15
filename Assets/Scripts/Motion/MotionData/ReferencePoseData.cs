using System;
using UnityEngine;

public readonly struct ReferencePoseData : ITimeSeriesData {
    public DateTime Timestamp { get; }
    public Quaternion Rotation { get; }
    public Vector3 Position { get; }
    public float EmaRotVelDeg { get; }
    public float EmaPosVelMps { get; }
    public bool IsStable { get; }
    public float StableAccumMs { get; }

    public ReferencePoseData(
        DateTime timestamp,
        Quaternion rotation,
        Vector3 position,
        float emaRotVelDeg,
        float emaPosVelMps,
        bool isStable,
        float stableAccumMs
    ){
        Timestamp = timestamp;
        Rotation = rotation;
        Position = position;
        EmaRotVelDeg = emaRotVelDeg;
        EmaPosVelMps = emaPosVelMps;
        IsStable = isStable;
        StableAccumMs = stableAccumMs;
    }
}


