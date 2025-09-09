using System;
using UnityEngine;

public sealed class Splat : IDisposable {
    public readonly GraphicsBuffer PointsBuffer; // Structured: float3 position_c0_m, float radius_m
    public readonly int Count;                   // Expected = width * height
    public readonly Guid JobId;                  // Async job id corresponding to depth

    public Splat(GraphicsBuffer pointsBuffer,
                 int count,
                 Guid jobId){
        if (pointsBuffer == null) throw new ArgumentNullException(nameof(pointsBuffer));
        if (!pointsBuffer.IsValid()) throw new InvalidOperationException("Splat: pointsBuffer is invalid");
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Splat: count must be > 0");
        PointsBuffer = pointsBuffer;
        Count = count;
        JobId = jobId;
    }

    public void Dispose(){
        if (PointsBuffer != null && PointsBuffer.IsValid()){
            PointsBuffer.Dispose();
        }
    }
}


