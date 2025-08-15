using System;
using UnityEngine;

public abstract class GpuDepthProvider : MonoBehaviour {
    private readonly int TICK_MAX = 1024;
    public abstract RenderTexture DepthTex{ get; }
    public abstract DateTime TimeStamp{ get; }
    public event Action<RenderTexture> OnDepthTexInit;

    public event Action<RenderTexture> OnDepthUpdated;

    public bool IsInitTexture{get; protected set;}

    public int Tick{ get; private set; }

    protected void TickUp(){
        Tick = (Tick + 1) % TICK_MAX;
        OnDepthUpdated?.Invoke(DepthTex);
    }

    protected virtual void OnDepthTexInitialized() {
        OnDepthTexInit?.Invoke(DepthTex);
    }
}