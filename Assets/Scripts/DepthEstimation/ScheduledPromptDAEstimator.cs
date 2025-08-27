using System;
using UnityEngine;

/// <summary>
/// Scheduler の状態に応じて PromptDA 推論を制御する Estimator。
/// 親は AsyncFrameProvider。内部処理は PromptDAIterableEstimator の流れを参考。
/// </summary>
public class ScheduledPromptDAEstimator : AsyncFrameProvider {
    [Header("Input Sources")]
    [SerializeField] private FrameProvider cameraRec;
    [SerializeField] private FrameProvider depthRec;
    [SerializeField] private PromptDAIterableProcessor processor;
    [SerializeField] private Scheduler scheduler;

    [Header("Sync Settings")]
    [SerializeField] private float maxTimeSyncDifferenceMs = 100f;

    [Header("Scheduling/Steps")]
    [SerializeField] private int stepsPerFrame = 4;

    [Header("Output Policy")]
    [SerializeField] private float highSpeedFillValue = -1f;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[SchedPromptDA]";

    private struct FrameData {
        public DateTime timestamp;
        public RenderTexture rgbFrame;
        public RenderTexture depthFrame;
        public DateTime rgbTimestamp;
        public DateTime depthTimestamp;
        public bool isValid;
    }

    private FrameData _latestRgb;
    private FrameData _latestDepth;

    // FrameProvider meta
    private DateTime _lastUpdateTime;
    private ScheduleStatus _prevState = ScheduleStatus.STOP;
    private Guid _lastProcessedCompletedId = Guid.Empty;
    public override RenderTexture FrameTex => processor != null ? processor.ResultRT : null;
    public override DateTime TimeStamp => _lastUpdateTime;

    private void Start(){
        ValidateSerializedFieldsOrThrow();
        SetupInputSubscriptions();
        if (processor.ResultRT != null) {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
        if (verboseLogging) 
            Debug.Log($"{logPrefix} Start: stepsPerFrameLow={stepsPerFrame}");
    }

    private void OnEnable(){
        ValidateSerializedFieldsOrThrow();
        SetupInputSubscriptions();
    }

    private void OnDisable(){
        if (cameraRec != null) cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
        if (depthRec  != null) depthRec.OnFrameUpdated  -= OnDepthFrameReceived;
    }

    private void SetupInputSubscriptions(){
        if (cameraRec != null) {
            cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
            cameraRec.OnFrameUpdated += OnRgbFrameReceived;
        }
        if (depthRec != null) {
            depthRec.OnFrameUpdated -= OnDepthFrameReceived;
            depthRec.OnFrameUpdated += OnDepthFrameReceived;
        }
    }

    private void OnRgbFrameReceived(RenderTexture rgbFrame){
        _latestRgb = new FrameData {
            timestamp      = cameraRec.TimeStamp,
            rgbFrame       = rgbFrame,
            rgbTimestamp   = cameraRec.TimeStamp,
            depthTimestamp = DateTime.MinValue,
            isValid        = true
        };
        TryKickOrPend();
    }

    private void OnDepthFrameReceived(RenderTexture depthFrame){
        _latestDepth = new FrameData {
            timestamp      = depthRec.TimeStamp,
            depthFrame     = depthFrame,
            rgbTimestamp   = DateTime.MinValue,
            depthTimestamp = depthRec.TimeStamp,
            isValid        = true
        };
        TryKickOrPend();
    }

    private void TryKickOrPend(){
        ValidateSerializedFieldsOrThrow();
        if (!_latestRgb.isValid || !_latestDepth.isValid)
            return;

        var state = scheduler.CurrentState;
        // Allow Begin in LOW_SPEED, or in STOP only when request targets the current job ID and it is not running
        bool allowBegin = (state == ScheduleStatus.LOW_SPEED) ||
                          (state == ScheduleStatus.STOP &&
                           scheduler.UpdateReqId != Guid.Empty &&
                           !processor.IsRunning &&
                           processor.CurrentJobId == scheduler.UpdateReqId);
        if (!allowBegin)
            return;

        var dtMs = Mathf.Abs((float)(_latestRgb.rgbTimestamp - _latestDepth.depthTimestamp).TotalMilliseconds);
        if (dtMs > maxTimeSyncDifferenceMs) 
            return;
        if (!processor.IsInitialized || processor.ResultRT == null) 
            return;
        
        var frame = new FrameData {
            timestamp      = (_latestRgb.rgbTimestamp > _latestDepth.depthTimestamp) ? _latestRgb.rgbTimestamp : _latestDepth.depthTimestamp,
            rgbFrame       = _latestRgb.rgbFrame,
            depthFrame     = _latestDepth.depthFrame,
            rgbTimestamp   = _latestRgb.rgbTimestamp,
            depthTimestamp = _latestDepth.depthTimestamp,
            isValid        = true
        };

        if (processor.Begin(frame.rgbFrame, frame.depthFrame, frame.timestamp)) {
            var jobId = ProcessStart();
            processor.SetJobId(jobId);

            if (verboseLogging) 
                Debug.Log($"{logPrefix} Begin OK: jobId={jobId}, ts={frame.timestamp:HH:mm:ss.fff}");
        } else {
            if (verboseLogging)
                Debug.LogWarning($"{logPrefix} Begin rejected by processor (already running or invalid)");
        }
        
        _latestRgb.isValid = false;
        _latestDepth.isValid = false;
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
        StepProcessor(stepsPerFrame);

        bool promotedNow = processor.TryPromoteCompleted();
        if (promotedNow) {
            var completedId = processor.CompletedJobId;
            if (completedId != Guid.Empty && completedId != _lastProcessedCompletedId) {
                if (processor.ResultRT != null){
                    _lastUpdateTime = DateTime.UtcNow;
                    if (verboseLogging)
                        Debug.Log($"{logPrefix} Complete: calling ProcessEnd for jobId={completedId}");
                    ProcessEnd(completedId);
                    _lastProcessedCompletedId = completedId;
                }
            }
        }
        _prevState = state;
    }

    private void ValidateSerializedFieldsOrThrow(){
        if (cameraRec == null) 
            throw new NullReferenceException("ScheduledPromptDAEstimator: cameraRec is not assigned");
        if (depthRec == null) 
            throw new NullReferenceException("ScheduledPromptDAEstimator: depthRec is not assigned");
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


