using System;
using UnityEngine;

public abstract class DepthModelIterableProcessor : MonoBehaviour {
    // States
    public abstract bool IsInitialized { get; }
    public abstract RenderTexture ResultRT { get; }
    public abstract DateTime CurrentTimestamp { get; }
    public abstract bool IsRunning { get; }
    public abstract Guid CurrentJobId { get; }
    public abstract Guid FinalizedJobId { get; }
    // 仕様: Processorが内部で遅延昇格を行い、昇格完了時にこの値を更新する
    public abstract Guid CompletedJobId { get; }

    // Starter
    public abstract void SetupInputSubscriptions();

    // Returns Guid.Empty if start was not permitted
    public abstract Guid TryStartProcessing();

    // Execution / cancel
    public abstract void Step(int steps);
    public abstract void InvalidateJob(Guid jobId);

    // Output Control
    public abstract void FillOutput(float value);
    public abstract void ClearOutput();
}