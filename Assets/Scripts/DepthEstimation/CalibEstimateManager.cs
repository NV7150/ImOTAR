using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class CalibEstimateManager : AsyncFrameProvider {
    [Header("Processor")]
    [SerializeField] private DepthModelIterableProcessor processor;
    [SerializeField, Min(1)] private int stepsPerFrame = 4;

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[CalibEstimate]";

    private DateTime _lastUpdateTime;
    private Guid _lastStartedJobId = Guid.Empty;
    private Guid _lastEndedJobId = Guid.Empty;
    private bool _run;

    public override RenderTexture FrameTex => processor != null ? processor.ResultRT : null;
    public override DateTime TimeStamp => _lastUpdateTime;

    private void OnEnable(){
        if (processor == null) throw new NullReferenceException("CalibEstimateManager: processor not assigned");
        processor.SetupInputSubscriptions();
        if (processor.ResultRT != null){
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
    }

    public void Resume(){ _run = true; TryStartIfIdle(); }
    public void Pause(){ _run = false; CancelCurrentIfAny(); }

    public void DiscardCurrEstimation(){ CancelCurrentIfAny(); }

    private void TryStartIfIdle(){
        if (!_run || processor == null || !processor.IsInitialized) return;
        if (processor.IsRunning) return;
        var startedId = processor.TryStartProcessing();
        if (startedId != Guid.Empty){
            _lastStartedJobId = startedId;
            ProcessStart(startedId);
            if (logVerbose) Debug.Log($"{logPrefix} Begin OK: jobId={startedId}");
        }
    }

    private void CancelCurrentIfAny(){
        if (processor == null || !processor.IsInitialized) return;
        if (processor.IsRunning){
            processor.InvalidateJob(processor.CurrentJobId);
        }
    }

    private void Update(){
        if (processor == null || !processor.IsInitialized) return;

        if (_run && processor.IsRunning){
            int n = Mathf.Max(1, stepsPerFrame);
            processor.Step(n);
        }

        var finalizedId = processor.FinalizedJobId;
        if (finalizedId != Guid.Empty && finalizedId == _lastStartedJobId && finalizedId != _lastEndedJobId){
            if (processor.ResultRT != null){
                _lastUpdateTime = DateTime.UtcNow;
                ProcessEnd(finalizedId);
                _lastEndedJobId = finalizedId;
            }
            // Immediately start next if running
            TryStartIfIdle();
        }
    }
}


