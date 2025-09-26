using System;
using System.ComponentModel;
using UnityEngine;

[DisallowMultipleComponent]
public class StateEstimateManager : AsyncFrameProvider {
    [Header("Processor")]
    [SerializeField] private DepthModelIterableProcessor processor;
    [SerializeField, Min(1)] private int stepsPerFrame = 4;

    [Header("State")]
    [SerializeField] private StateManager state;

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[StateEstimate]";

    private DateTime _lastUpdateTime;
    private Guid _lastStartedJobId = Guid.Empty;
    private Guid _lastEndedJobId = Guid.Empty;
    public override RenderTexture FrameTex => processor != null ? processor.ResultRT : null;
    public override DateTime TimeStamp => _lastUpdateTime;

    private void OnEnable(){
        if (processor == null) throw new NullReferenceException("StateEstimateManager: processor not assigned");
        if (state == null) throw new NullReferenceException("StateEstimateManager: state not assigned");
        processor.SetupInputSubscriptions();
        state.TryGenerate += TryBirth;
        state.OnGenerate += OnBirth;
        state.OnDiscard  += OnDead; // we keep result, no events fired (we won't TickUp here)

        if (processor.ResultRT != null) {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
    }

    private void OnDisable(){
        state.TryGenerate -= TryBirth;
        state.OnGenerate -= OnBirth;
        state.OnDiscard  -= OnDead;
    }

    private bool TryBirth(){
        return processor.IsInitialized && !processor.IsRunning;
    }

    private void OnBirth(){
        Debug.Log($"{logPrefix} OnBirth called");

        if(!TryBirth())
            throw new InvalidAsynchronousStateException("Invalid Birth");

        var startedId = processor.TryStartProcessing();
        if (startedId != Guid.Empty){
            _lastStartedJobId = startedId;
            ProcessStart(startedId);
            if (logVerbose) Debug.Log($"{logPrefix} Begin OK: jobId={startedId}");
        } else {
            state.GenerateFailed();
        }
    }

    private void OnDead(){
        // Do nothing: keep last output, do not invalidate, and do not emit frame events.
        if (logVerbose) Debug.Log($"{logPrefix} OnDead: keep output, no events");
    }

    private void Update(){
        if (processor == null || !processor.IsInitialized) {
            if (logVerbose) Debug.Log($"{logPrefix} Update: processor null or not initialized (procNull={processor==null})");
            return;
        }

        // Always advance running job; state gating is already in Birth/Dead
        if (processor.IsRunning){
            int n = Mathf.Max(1, stepsPerFrame);
            if (logVerbose) Debug.Log($"{logPrefix} Update: Step({n}) currentJob={processor.CurrentJobId}");
            processor.Step(n);
        }

        // Finalize-driven completion
        var finalizedId = processor.FinalizedJobId;
        if (logVerbose) 
            Debug.Log($"{logPrefix} Update: state={state.CurrState} running={processor.IsRunning} lastStarted={_lastStartedJobId} finalized={finalizedId} lastEnded={_lastEndedJobId}");
        if (finalizedId != Guid.Empty && finalizedId == _lastStartedJobId && finalizedId != _lastEndedJobId) {
            if (processor.ResultRT != null){
                _lastUpdateTime = DateTime.UtcNow;
                if (logVerbose) Debug.Log($"{logPrefix} Finalized: calling ProcessEnd for jobId={finalizedId}");
                ProcessEnd(finalizedId);
                _lastEndedJobId = finalizedId;
                // Transition to ALIVE
                if (logVerbose) Debug.Log($"{logPrefix} Calling state.BirthEnd() for jobId={finalizedId}");
                state.GenerateEnd();
            }
        } else if (logVerbose) {
            // Detail why not finalized
            if (finalizedId == Guid.Empty) Debug.Log($"{logPrefix} Update: no finalized job yet");
            else if (finalizedId != _lastStartedJobId) Debug.Log($"{logPrefix} Update: finalizedId != lastStarted (finalized={finalizedId}, lastStarted={_lastStartedJobId})");
            else if (finalizedId == _lastEndedJobId) Debug.Log($"{logPrefix} Update: finalized already ended (id={finalizedId})");
        }
    }
}


