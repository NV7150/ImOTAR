using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class NaiveEstimateManager : AsyncFrameProvider {
    [Header("Processor")]
    [SerializeField] private DepthModelIterableProcessor processor;
    [SerializeField, Min(1)] private int stepsPerFrame = 4;

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[NaiveEstimate]";

    private DateTime _lastUpdateTime;
    private Guid _lastStartedJobId = Guid.Empty;
    private Guid _lastEndedJobId = Guid.Empty;

    public override RenderTexture FrameTex => processor != null ? processor.ResultRT : null;
    public override DateTime TimeStamp => _lastUpdateTime;

    private void OnEnable(){
        if (processor == null) throw new NullReferenceException("NaiveEstimateManager: processor not assigned");
        processor.SetupInputSubscriptions();

        if (processor.ResultRT != null){
            IsInitTexture = true;
            OnFrameTexInitialized();
        }

        // Start initial job
        TryStartNewJob();
    }

    private void TryStartNewJob(){
        if (processor == null || !processor.IsInitialized) return;
        if (processor.IsRunning) return;

        var startedId = processor.TryStartProcessing();
        if (startedId != Guid.Empty){
            _lastStartedJobId = startedId;
            ProcessStart(startedId);
            if (logVerbose) Debug.Log($"{logPrefix} Begin OK: jobId={startedId}");
        }
    }

    private void Update(){
        if (processor == null || !processor.IsInitialized) return;

        // Advance running job
        if (processor.IsRunning){
            int n = Mathf.Max(1, stepsPerFrame);
            processor.Step(n);
        }

        // Check for finalized job and immediately start next
        var finalizedId = processor.FinalizedJobId;
        if (finalizedId != Guid.Empty && finalizedId == _lastStartedJobId && finalizedId != _lastEndedJobId){
            if (processor.ResultRT != null){
                _lastUpdateTime = DateTime.UtcNow;
                ProcessEnd(finalizedId);
                _lastEndedJobId = finalizedId;
                if (logVerbose) Debug.Log($"{logPrefix} Finalized: jobId={finalizedId}");
            }
            // Immediately start next job
            TryStartNewJob();
        }

        // If not running and no job pending, start new job
        if (!processor.IsRunning){
            TryStartNewJob();
        }
    }
}

