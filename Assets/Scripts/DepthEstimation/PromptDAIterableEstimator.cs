using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// イテレータブル実行版 Estimator
/// - RGB/Depth を同期 → processor.Begin()
/// - Update で stepsPerFrame だけ Step() して前進
/// - 完了(IsComplete)後に出力へ CopyTexture → FrameProvider更新(TickUp)
/// - 実行中に来た新規フレームは latest-wins で1本だけペンディング
/// </summary>
public class PromptDAIterableEstimator : FrameProvider
{
    [Header("Input Sources")]
    [SerializeField] private FrameProvider cameraRec;     // RGB
    [SerializeField] private FrameProvider depthRec;      // Depth(meters)
    [SerializeField] private PromptDAIterableProcessor processor;

    // 出力は Processor 側で保持する outputRT を利用する

    [Header("Sync Settings")]
    [SerializeField] private float maxTimeSyncDifferenceMs = 100f;

    [Header("Scheduling")]
    [Tooltip("1フレームあたりの前進ステップ数（イテレータのMoveNext回数）")]
    [SerializeField] private int stepsPerFrame = 4;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[PromptDA-EST]";

    // 入力同期
    private struct FrameData
    {
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
    public override RenderTexture FrameTex => processor != null ? processor.ResultRT : null;
    public override DateTime TimeStamp => _lastUpdateTime;

    void Start()
    {
        SetupInputSubscriptions();

        if (processor != null && processor.ResultRT != null)
        {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }

        if (verboseLogging)
        {
            Debug.Log($"{logPrefix} Start: stepsPerFrame={stepsPerFrame}, platform={Application.platform}");
        }
    }

    void OnEnable() => SetupInputSubscriptions();

    void OnDisable()
    {
        if (cameraRec != null) cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
        if (depthRec  != null) depthRec.OnFrameUpdated  -= OnDepthFrameReceived;
    }

    void SetupInputSubscriptions()
    {
        if (cameraRec != null)
        {
            cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
            cameraRec.OnFrameUpdated += OnRgbFrameReceived;
        }
        if (depthRec != null)
        {
            depthRec.OnFrameUpdated -= OnDepthFrameReceived;
            depthRec.OnFrameUpdated += OnDepthFrameReceived;
        }
        if (verboseLogging)
        {
            // Debug.Log($"{logPrefix} SetupInputSubscriptions: cameraRec={(cameraRec!=null)}, depthRec={(depthRec!=null)}");
        }
    }

    void OnRgbFrameReceived(RenderTexture rgbFrame)
    {
        _latestRgb = new FrameData
        {
            timestamp      = cameraRec.TimeStamp,
            rgbFrame       = rgbFrame,
            rgbTimestamp   = cameraRec.TimeStamp,
            depthTimestamp = DateTime.MinValue,
            isValid        = true
        };
        // if (verboseLogging) Debug.Log($"{logPrefix} OnRgbFrameReceived: ts={_latestRgb.rgbTimestamp:HH:mm:ss.fff}, size={rgbFrame.width}x{rgbFrame.height}");
        TryKickOrPend();
    }

    void OnDepthFrameReceived(RenderTexture depthFrame)
    {
        _latestDepth = new FrameData
        {
            timestamp      = depthRec.TimeStamp,
            depthFrame     = depthFrame,
            rgbTimestamp   = DateTime.MinValue,
            depthTimestamp = depthRec.TimeStamp,
            isValid        = true
        };
        // if (verboseLogging) Debug.Log($"{logPrefix} OnDepthFrameReceived: ts={_latestDepth.depthTimestamp:HH:mm:ss.fff}, size={depthFrame.width}x{depthFrame.height}");
        TryKickOrPend();
    }

    void TryKickOrPend()
    {
        if (!_latestRgb.isValid || !_latestDepth.isValid)
        {
            // if (verboseLogging) Debug.Log($"{logPrefix} TryKickOrPend: waiting other stream (rgbValid={_latestRgb.isValid}, depValid={_latestDepth.isValid})");
            return;
        }

        var dtMs = Mathf.Abs((float)(_latestRgb.rgbTimestamp - _latestDepth.depthTimestamp).TotalMilliseconds);
        if (dtMs > maxTimeSyncDifferenceMs)
        {
            // if (verboseLogging) Debug.Log($"{logPrefix} TryKickOrPend: drop (dtMs={dtMs:0.0} > {maxTimeSyncDifferenceMs})");
            return;
        }
        if (processor == null || !processor.IsInitialized || processor.ResultRT == null)
        {
            // if (verboseLogging) Debug.LogWarning($"{logPrefix} TryKickOrPend: invalid state (procNull={processor==null}, procInit={processor?.IsInitialized}, outRTNull={outputRT==null})");
            return;
        }

        var frame = new FrameData
        {
            timestamp      = (_latestRgb.rgbTimestamp > _latestDepth.depthTimestamp) ? _latestRgb.rgbTimestamp : _latestDepth.depthTimestamp,
            rgbFrame       = _latestRgb.rgbFrame,
            depthFrame     = _latestDepth.depthFrame,
            rgbTimestamp   = _latestRgb.rgbTimestamp,
            depthTimestamp = _latestDepth.depthTimestamp,
            isValid        = true
        };

        if (processor.IsRunning)
        {
            // 実行中 → ペンディングへ（最新勝ち）
            _pending = frame;
            _hasPending = true;
            // if (verboseLogging) Debug.Log($"{logPrefix} TryKickOrPend: queued pending (ts={frame.timestamp:HH:mm:ss.fff})");
        }
        else
        {
            // 空いていれば即開始
            if (processor.Begin(frame.rgbFrame, frame.depthFrame, frame.timestamp))
            {
                _hasPending = false;
                // if (verboseLogging) Debug.Log($"{logPrefix} TryKickOrPend: Begin OK (ts={frame.timestamp:HH:mm:ss.fff})");
            }
            else
            {
                _pending = frame;
                _hasPending = true;
                // if (verboseLogging) Debug.LogWarning($"{logPrefix} TryKickOrPend: Begin rejected -> pending");
            }
        }

        _latestRgb.isValid = false;
        _latestDepth.isValid = false;
    }

    void Update()
    {
        if (processor == null || !processor.IsInitialized) return;

        // 開発時のみ、フェンス状態を安全に監視（例外耐性あり）
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (verboseLogging && processor != null)
        {
            bool ok = processor.TryGetFencePassed(out var fencePassed);
            if ((Time.frameCount % 15) == 0)
            {
                Debug.Log($"{logPrefix} Monitor: running={processor.IsRunning}, finalized={processor.IsFinalized}, complete={processor.IsComplete}, fenceOk={ok}, fencePassed={fencePassed}");
            }
        }
        #endif

        // 実行中なら所定ステップだけ前進
        if (processor.IsRunning)
        {
            int n = Mathf.Max(1, stepsPerFrame);
            processor.Step(n);
        }
        else
        {
            // if (verboseLogging) Debug.Log($"{logPrefix} Update: processor not running (complete={processor.IsComplete})");
        }

        // 完了したら適用→FrameProvider更新→次へ
        if (processor.IsComplete)
        { 
            // 結果は Processor が outputRT へ直接書く
            if (processor.ResultRT != null)
            {
                _lastUpdateTime = DateTime.UtcNow;
                TickUp();

                if (verboseLogging) Debug.Log($"{logPrefix} Apply: outputRT updated at frame={Time.frameCount}, time={Time.unscaledTime:0.000}");
            }
            else
            {
                if (verboseLogging) Debug.LogWarning($"{logPrefix} Apply: skip (ResultRT null={processor.ResultRT==null})");
            }

            // 次ジョブのために状態を初期化
            processor.ResetForNext();

            // ペンディングがあればキック
            if (_hasPending)
            {
                if (processor.Begin(_pending.rgbFrame, _pending.depthFrame, _pending.timestamp))
                {
                    if (verboseLogging) Debug.Log($"{logPrefix} Begin (from pending): ts={_pending.timestamp:HH:mm:ss.fff}");
                    _hasPending = false;
                }
                else
                {
                    if (verboseLogging) Debug.LogWarning($"{logPrefix} Begin (from pending): rejected; will retry on next update");
                }
            }
        }
    }
}
