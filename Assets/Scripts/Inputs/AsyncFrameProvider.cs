
using System;
using UnityEngine;

public abstract class AsyncFrameProvider: FrameProvider{
    public event Action<Guid> OnAsyncFrameStarted;

    public event Action<AsyncFrame> OnAsyncFrameUpdated;

    public event Action<Guid> OnAsyncFrameCanceled;

    protected Guid ProcessStart(){
        Guid frameId = Guid.NewGuid();
        OnAsyncFrameStarted?.Invoke(frameId);
        return frameId;
    }

    protected Guid ProcessStart(Guid id){
        OnAsyncFrameStarted?.Invoke(id);
        return id;
    }

    protected void ProcessEnd(Guid id){
        var frame = new AsyncFrame(id, FrameTex);
        OnAsyncFrameUpdated?.Invoke(frame);
        TickUp();
    }

    protected void ProcessCanceled(Guid id){
        OnAsyncFrameCanceled?.Invoke(id);
    }



}

public struct AsyncFrame{
    private Guid id;
    private RenderTexture renderTexture;

    public AsyncFrame(Guid id, RenderTexture renderTexture) {
        this.id = id;
        this.renderTexture = renderTexture;
    }

    public Guid Id => id;
    public RenderTexture RenderTexture => renderTexture;
}