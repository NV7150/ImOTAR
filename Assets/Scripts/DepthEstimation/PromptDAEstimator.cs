using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// PromptDA深度推定のスケジューラー（非待機）
/// - RGB/Depth を同期し、PromptDAProcessor に Submit（非待機）
/// - 適用は GPU 内で同期（Async 対応時）または単一キュー順序に依存（非対応時）
/// </summary>
public class PromptDAEstimator : FrameProvider
{
    [Header("Input Sources")]
    [SerializeField] private FrameProvider cameraRec;     // RGB
    [SerializeField] private FrameProvider depthRec;      // Depth (meters)
    [SerializeField] private PromptDAProcessor processor; // モデル処理

    [Header("Output")]
    [SerializeField] private RenderTexture outputRT;

    [Header("Sync Settings")]
    [SerializeField] private float maxTimeSyncDifferenceMs = 100f;
    [SerializeField] private int maxQueueSize = 8;

    [Header("Performance Settings")]
    [SerializeField] private int maxProcessPerFrame = 2;   // in-flight 解放チェック上限/フレーム
    [SerializeField] private bool autoReleaseTextures = true; // 予約（この実装では未使用）

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

    private Queue<FrameData> _frameQueue = new Queue<FrameData>();
    private FrameData _latestRgb;
    private FrameData _latestDepth;

    // In-flight（GPU実行中）ジョブ
    private readonly List<PromptDAProcessor.InflightJob> _inflight = new();

    // 出力の最新時刻
    private DateTime _latestOutputTimestamp = DateTime.MinValue;
    private DateTime _lastUpdateTime;

    public override RenderTexture FrameTex => outputRT;
    public override DateTime TimeStamp => _lastUpdateTime;

    void Start()
    {
        SetupInputSubscriptions();
        if (outputRT != null)
        {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
    }

    void OnEnable() => SetupInputSubscriptions();

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
    }

    void OnRgbFrameReceived(RenderTexture rgbFrame)
    {
        _latestRgb = new FrameData
        {
            timestamp = cameraRec.TimeStamp,
            rgbFrame = rgbFrame,
            rgbTimestamp = cameraRec.TimeStamp,
            depthTimestamp = DateTime.MinValue,
            isValid = true
        };
        TryCreateProcessingJob();
    }

    void OnDepthFrameReceived(RenderTexture depthFrame)
    {
        _latestDepth = new FrameData
        {
            timestamp = depthRec.TimeStamp,
            depthFrame = depthFrame,
            rgbTimestamp = DateTime.MinValue,
            depthTimestamp = depthRec.TimeStamp,
            isValid = true
        };
        TryCreateProcessingJob();
    }

    void TryCreateProcessingJob()
    {
        if (!_latestRgb.isValid || !_latestDepth.isValid) return;

        var timeDiffMs = Mathf.Abs((float)(_latestRgb.rgbTimestamp - _latestDepth.depthTimestamp).TotalMilliseconds);
        if (timeDiffMs > maxTimeSyncDifferenceMs) return;

        if (processor == null || !processor.IsInitialized) return;

        var frameData = new FrameData
        {
            timestamp = (_latestRgb.rgbTimestamp > _latestDepth.depthTimestamp) ? _latestRgb.rgbTimestamp : _latestDepth.depthTimestamp,
            rgbFrame = _latestRgb.rgbFrame,
            depthFrame = _latestDepth.depthFrame,
            rgbTimestamp = _latestRgb.rgbTimestamp,
            depthTimestamp = _latestDepth.depthTimestamp,
            isValid = true
        };

        _frameQueue.Enqueue(frameData);
        while (_frameQueue.Count > maxQueueSize) _frameQueue.Dequeue();

        // ==== 非待機 Submit ====
        if (processor.TrySubmit(frameData.rgbFrame, frameData.depthFrame, frameData.timestamp, out var job))
        {
            // 適用CB：GPU内だけで同期（Async対応時）/ 単一キュー（非対応時）
            var apply = new CommandBuffer { name = "Apply PromptDA Result" };
            if (processor.SupportsAsyncCompute)
            {
                apply.WaitOnAsyncGraphicsFence(job.fence); // GPU だけが待つ
            }
            apply.CopyTexture(job.result, outputRT);
            Graphics.ExecuteCommandBuffer(apply); // CPUは即時復帰

            // 見かけ上の最新更新（GPU完了と厳密同期は取らない）
            if (job.timestamp > _latestOutputTimestamp)
            {
                _latestOutputTimestamp = job.timestamp;
                _lastUpdateTime = DateTime.UtcNow;
                TickUp();
            }

            _inflight.Add(job);
        }

        // 使用済みをリセット
        _latestRgb.isValid = false;
        _latestDepth.isValid = false;
    }

    void Update() => ProcessResultQueueOptimized();

    // in-flight の解放のみ（CPUは待たない）
    void ProcessResultQueueOptimized()
    {
        int checkedCount = 0;
        for (int i = _inflight.Count - 1; i >= 0 && checkedCount < maxProcessPerFrame; i--)
        {
            var job = _inflight[i];
            // Async対応: AsyncFence.passed / 非対応: CPUSyncFence.passed（いずれも安全）
            if (job.fence.passed)
            {
                processor.ReleaseWorkerIfComplete(job);
                _inflight.RemoveAt(i);
            }
            checkedCount++;
        }
    }

    void OnDestroy()
    {
        if (cameraRec != null) cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
        if (depthRec != null)  depthRec.OnFrameUpdated  -= OnDepthFrameReceived;
        _inflight.Clear();
    }

    void OnDisable()
    {
        if (cameraRec != null) cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
        if (depthRec != null)  depthRec.OnFrameUpdated  -= OnDepthFrameReceived;
    }
}
