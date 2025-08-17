using System;
using UnityEngine;

public abstract class FrameProvider : MonoBehaviour {
    private readonly int TICK_MAX = 1024;
    public abstract RenderTexture FrameTex{ get; }
    public abstract DateTime TimeStamp{ get; }
    public event Action<RenderTexture> OnFrameTexInit;

    public event Action<RenderTexture> OnFrameUpdated;

    public bool IsInitTexture{get; protected set;}

    public int Tick{ get; private set; }

    protected void TickUp(){
        Tick = (Tick + 1) % TICK_MAX;
        OnFrameUpdated?.Invoke(FrameTex);
    }

    protected virtual void OnFrameTexInitialized() {
        OnFrameTexInit?.Invoke(FrameTex);
    }
}
