using System;
using UnityEngine;

public abstract class RegionProvider : MonoBehaviour {
    public abstract int CurrentRegionCount { get; }
    public abstract RenderTexture CurrentRegion { get; }
    public abstract int Tick { get;}

    public event Action<RenderTexture> OnUpdated;

    protected void InvokeTexUp(RenderTexture tex){
        OnUpdated?.Invoke(tex);
    }
}