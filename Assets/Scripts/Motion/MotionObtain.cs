using System;
using UnityEngine;

public abstract class MotionObtain : MonoBehaviour {
    public abstract bool TryGetLatestData<T>(out T data) where T : struct, ITimeSeriesData;
    public abstract int CopyHistory<T>(DateTime from, DateTime to, Span<T> dst) where T : struct, ITimeSeriesData;
    public abstract int CopyLastN<T>(int n, Span<T> dst) where T : struct, ITimeSeriesData;
}


