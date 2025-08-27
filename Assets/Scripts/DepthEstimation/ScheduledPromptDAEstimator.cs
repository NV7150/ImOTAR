using System;
using UnityEngine;

/// <summary>
/// Scheduler の状態に応じて PromptDA 推論を制御する Estimator。
/// 親は AsyncFrameProvider。内部処理は PromptDAIterableEstimator の流れを参考。
/// </summary>
public class ScheduledPromptDAEstimator : AsyncFrameProvider {
    [Header("Input Sources")]
    [SerializeField] private FrameProvider cameraRec;     // RGB
    [SerializeField] private FrameProvider depthRec;      // Depth(meters)
    [SerializeField] private PromptDAIterableProcessor processor;
    [SerializeField] private SchedulerBase scheduler;

    [Header("Sync Settings")]
    [SerializeField] private float maxTimeSyncDifferenceMs = 100f;

    [Header("Scheduling/Steps")]
    [Tooltip("LOW_SPEED 時に 1 フレームあたり回すステップ数")]
    [SerializeField] private int stepsPerFrameLow = 4;
    [Tooltip("HIGH→STOP 遷移時の1回限りのステップ数（将来強化用の余地）")]
    [SerializeField] private int oneShotStepsOnStop = 4;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[SchedPromptDA]";

    // 入力同期
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

    // ペンディング（進行中に来た最新1本）
    private bool _hasPending = false;
    private FrameData _pending;

    // FrameProviderメタ
    private DateTime _lastUpdateTime;
    private System.Guid _currentJobId = System.Guid.Empty;
    private ScheduleStatus _prevState = ScheduleStatus.STOP;
    private bool _runOnceAfterHighToStop = false;
    public override RenderTexture FrameTex => processor != null ? processor.ResultRT : null;
    public override DateTime TimeStamp => _lastUpdateTime;

    private void Start(){
        ValidateSerializedFieldsOrThrow();
        SetupInputSubscriptions();
        if (processor.ResultRT != null) {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
        if (verboseLogging) Debug.Log($"{logPrefix} Start: stepsPerFrameLow={stepsPerFrameLow}");
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
        if (!_latestRgb.isValid || !_latestDepth.isValid) return;

        // LOW_SPEED のみ Begin を許可（STOP/HIGH_SPEED は開始しない）
        var state = scheduler.CurrentState;
        if (state != ScheduleStatus.LOW_SPEED) return;

        // 入力同期（PromptDAIterableEstimator と同等の閾値）
        var dtMs = Mathf.Abs((float)(_latestRgb.rgbTimestamp - _latestDepth.depthTimestamp).TotalMilliseconds);
        if (dtMs > maxTimeSyncDifferenceMs) return;
        if (!processor.IsInitialized || processor.ResultRT == null) return;

        var frame = new FrameData {
            timestamp      = (_latestRgb.rgbTimestamp > _latestDepth.depthTimestamp) ? _latestRgb.rgbTimestamp : _latestDepth.depthTimestamp,
            rgbFrame       = _latestRgb.rgbFrame,
            depthFrame     = _latestDepth.depthFrame,
            rgbTimestamp   = _latestRgb.rgbTimestamp,
            depthTimestamp = _latestDepth.depthTimestamp,
            isValid        = true
        };

        bool isRunningLike = (processor.FinalizedJobId == System.Guid.Empty) && (processor.CurrentJobId != System.Guid.Empty);
        if (isRunningLike) {
            _pending = frame;
            _hasPending = true;
        } else {
            if (processor.Begin(frame.rgbFrame, frame.depthFrame, frame.timestamp)) {
                _hasPending = false;
                _currentJobId = ProcessStart();
                processor.SetJobId(_currentJobId);
                if (verboseLogging) Debug.Log($"{logPrefix} Begin OK: jobId={_currentJobId}, ts={frame.timestamp:HH:mm:ss.fff}");
            } else {
                _pending = frame;
                _hasPending = true;
            }
        }

        _latestRgb.isValid = false;
        _latestDepth.isValid = false;
    }

    private void Update(){
        ValidateSerializedFieldsOrThrow();
        if (!processor.IsInitialized) return;

        var state = scheduler.CurrentState;
        if (_prevState == ScheduleStatus.HIGH_SPEED && state == ScheduleStatus.STOP){
            _runOnceAfterHighToStop = true;
        }
        switch (state){
            case ScheduleStatus.HIGH_SPEED:
                // 実行中ジョブを進めない（実質破棄）。出力は-1で塗る。
                if (processor.CurrentJobId != System.Guid.Empty || processor.FinalizedJobId != System.Guid.Empty)
                {
                    processor.AbortCurrent(clearOutputRT: false);
                    _currentJobId = System.Guid.Empty;
                    _hasPending = false; // 保留も破棄
                }
                FillOutputWithMinusOne();
                break;

            case ScheduleStatus.LOW_SPEED:
                StepProcessor(stepsPerFrameLow);
                break;

            case ScheduleStatus.STOP:
                // 以前のマスクを維持。挙動は HIGH_SPEED と同様に新規開始・実行を停止し、
                // HIGH→STOP 遷移直後のみ 1 回だけ前進してから中断する。
                if (_runOnceAfterHighToStop && processor.CurrentJobId != System.Guid.Empty){
                    StepProcessor(Mathf.Max(1, oneShotStepsOnStop));
                    _runOnceAfterHighToStop = false;
                }
                if (processor.CurrentJobId != System.Guid.Empty || processor.FinalizedJobId != System.Guid.Empty){
                    processor.AbortCurrent(clearOutputRT: false);
                    _currentJobId = System.Guid.Empty;
                }
                _hasPending = false;
                break;
        }

        // finalize→complete 昇格と適用
        processor.TryPromoteCompleted();
        bool isCompleteLike = (processor.CompletedJobId != System.Guid.Empty);
        if (isCompleteLike) {
            if (processor.ResultRT != null){
                _lastUpdateTime = DateTime.UtcNow;
                if (_currentJobId != System.Guid.Empty){
                    if (verboseLogging) Debug.Log($"{logPrefix} Complete: calling ProcessEnd for jobId={_currentJobId}");
                    ProcessEnd(_currentJobId);
                    _currentJobId = System.Guid.Empty;
                }
            }

            // ペンディングがあり、かつ LOW_SPEED のときのみ次ジョブへ
            if (_hasPending && scheduler.CurrentState == ScheduleStatus.LOW_SPEED){
                if (processor.Begin(_pending.rgbFrame, _pending.depthFrame, _pending.timestamp)){
                    _currentJobId = ProcessStart();
                    processor.SetJobId(_currentJobId);
                    if (verboseLogging) Debug.Log($"{logPrefix} Begin (pending): jobId={_currentJobId}");
                    _hasPending = false;
                }
            }
        }
        _prevState = state;
    }

    private void ValidateSerializedFieldsOrThrow(){
        if (cameraRec == null) throw new NullReferenceException("ScheduledPromptDAEstimator: cameraRec is not assigned");
        if (depthRec == null) throw new NullReferenceException("ScheduledPromptDAEstimator: depthRec is not assigned");
        if (processor == null) throw new NullReferenceException("ScheduledPromptDAEstimator: processor is not assigned");
        if (scheduler == null) throw new NullReferenceException("ScheduledPromptDAEstimator: scheduler is not assigned");
    }

    private void StepProcessor(int steps){
        bool isRunning = (processor.CurrentJobId != System.Guid.Empty);
        if (isRunning){
            int n = Mathf.Max(1, steps);
            processor.Step(n);
            if (verboseLogging) Debug.Log($"{logPrefix} Step: n={n}");
        }
    }

    private void FillOutputWithMinusOne(){
        var rt = processor != null ? processor.ResultRT : null;
        if (rt == null) return;
        var active = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, new Color(-1f, -1f, -1f, 1f));
        RenderTexture.active = active;
    }
}


