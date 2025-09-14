using System;
using UnityEngine;

/// <summary>
/// Scheduler の状態に応じて PromptDA 推論を制御する Estimator。
/// 親は AsyncFrameProvider。内部処理は PromptDAIterableEstimator の流れを参考。
/// </summary>
public class ScheduledEstimateManager : AsyncFrameProvider {
    [Header("Processor")]
    [SerializeField] private DepthModelIterableProcessor processor;
    [SerializeField] private Scheduler scheduler;

    [Header("Scheduling/Steps")]
    [SerializeField] private int stepsPerFrame = 4;

    [Header("Output Policy")]
    [SerializeField] private float highSpeedFillValue = -1f;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[SchedPromptDA]";

    // FrameProvider meta
    private DateTime _lastUpdateTime;
    private ScheduleStatus _prevState = ScheduleStatus.STOP;
    private Guid _lastStartedJobId = Guid.Empty;
    private Guid _lastEndedJobId = Guid.Empty;
    public override RenderTexture FrameTex => processor != null ? processor.ResultRT : null;
    public override DateTime TimeStamp => _lastUpdateTime;

    private void Start(){
        ValidateSerializedFieldsOrThrow();
        processor.SetupInputSubscriptions();
        if (processor.ResultRT != null) {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
        if (verboseLogging) 
            Debug.Log($"{logPrefix} Start: stepsPerFrameLow={stepsPerFrame}");
    }

    private void OnEnable(){
        ValidateSerializedFieldsOrThrow();
        processor.SetupInputSubscriptions();
    }

    private void OnDisable(){
        // Processor owns input subscriptions now
    }

    private void Update(){
        ValidateSerializedFieldsOrThrow();
        if (!processor.IsInitialized)
            return;

        var state = scheduler.CurrentState;
        // On enter HIGH_SPEED: immediately set output to a constant and invalidate current job by JobID
        if (_prevState != ScheduleStatus.HIGH_SPEED && state == ScheduleStatus.HIGH_SPEED){
            FillOutput(highSpeedFillValue);
            if (processor.CurrentJobId != Guid.Empty)
                processor.InvalidateJob(processor.CurrentJobId);
        }
        // Poll to start when allowed
        bool allowBegin = (state == ScheduleStatus.LOW_SPEED) ||
                          (state == ScheduleStatus.STOP &&
                           scheduler.UpdateReqId != Guid.Empty &&
                           !processor.IsRunning &&
                           processor.CurrentJobId == scheduler.UpdateReqId);
        if (allowBegin){
            var startedId = processor.TryStartProcessing();
            if (startedId != Guid.Empty){
                _lastStartedJobId = startedId;
                ProcessStart(startedId);
                if (verboseLogging)
                    Debug.Log($"{logPrefix} Begin OK: jobId={startedId}");
            }
        }

        StepProcessor(stepsPerFrame);

        // Finalize-driven completion
        var finalizedId = processor.FinalizedJobId;
        if (finalizedId != Guid.Empty && finalizedId == _lastStartedJobId && finalizedId != _lastEndedJobId) {
            if (processor.ResultRT != null){
                _lastUpdateTime = DateTime.UtcNow;
                if (verboseLogging)
                    Debug.Log($"{logPrefix} Finalized: calling ProcessEnd for jobId={finalizedId}");
                ProcessEnd(finalizedId);
                _lastEndedJobId = finalizedId;
            }
        }
        _prevState = state;
    }

    private void ValidateSerializedFieldsOrThrow(){
        if (processor == null)
            throw new NullReferenceException("ScheduledPromptDAEstimator: processor is not assigned");
        if (scheduler == null)
            throw new NullReferenceException("ScheduledPromptDAEstimator: scheduler is not assigned");
    }

    private void StepProcessor(int steps){
        bool isRunning = processor != null && processor.IsRunning;
        if (isRunning){
            int n = Mathf.Max(1, steps);
            processor.Step(n);
            if (verboseLogging) Debug.Log($"{logPrefix} Step: n={n}");
        }
    }

    private void FillOutput(float value){
        var rt = processor != null ? processor.ResultRT : null;
        if (rt == null)
            return;
        var active = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, new Color(value, value, value, 1f));
        RenderTexture.active = active;
    }
}


