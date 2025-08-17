using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// PromptDA深度推定のスケジューラー
/// RGB・深度ストリーム（meters単位）を同期し、PromptDAProcessorに処理を依頼する
/// </summary>
public class PromptDAEstimator: FrameProvider
{
    [Header("Input Sources")]
    [SerializeField] private FrameProvider cameraRec;     // RGB ストリーム
    [SerializeField] private FrameProvider depthRec;       // 深度ストリーム（meters単位）
    [SerializeField] private PromptDAProcessor processor; // モデル処理

    [Header("Output")]
    [SerializeField] private RenderTexture outputRT;  // 推論結果出力先

    [Header("Sync Settings")]
    [SerializeField] private float maxTimeSyncDifferenceMs = 100f;  // RGB-深度同期許容時間差(ms)
    [SerializeField] private int maxQueueSize = 8;  // キューサイズ上限

    [Header("Performance Settings")]
    [SerializeField] private int maxProcessPerFrame = 2;  // 1フレームあたりの最大処理数
    [SerializeField] private bool autoReleaseTextures = true;  // 使用済みテクスチャの自動解放

    // ストリーム同期用
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
    
    // 結果管理
    private struct ProcessingJob
    {
        public DateTime timestamp;
        public RenderTexture result;
        public bool isCompleted;
    }
    
    private Queue<ProcessingJob> _completedJobs = new Queue<ProcessingJob>();
    
    // 最新結果追跡用
    private DateTime _latestOutputTimestamp = DateTime.MinValue;
    
    private DateTime _lastUpdateTime;

    // FrameProviderの抽象プロパティを実装
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

    void OnEnable()
    {
        SetupInputSubscriptions();
    }

    void SetupInputSubscriptions()
    {
        // 重複購読を防ぐため、まず解除してから購読
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
            depthFrame = null,
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
            rgbFrame = null,
            depthFrame = depthFrame,
            rgbTimestamp = DateTime.MinValue,
            depthTimestamp = depthRec.TimeStamp,
            isValid = true
        };
        
        TryCreateProcessingJob();
    }

    void TryCreateProcessingJob()
    {
        // 両方のデータが揃っているかチェック
        if (!_latestRgb.isValid || !_latestDepth.isValid) return;
        
        // TimeStampが近い（同期している）かチェック
        var timeDiff = Mathf.Abs((float)(_latestRgb.rgbTimestamp - _latestDepth.depthTimestamp).TotalMilliseconds);
        if (timeDiff > maxTimeSyncDifferenceMs) return; // 設定時間以上のずれは許容しない
        
        // プロセッサが利用可能かチェック
        if (processor == null || !processor.IsInitialized) return;
        
        // 新しいフレームデータを作成
        var frameData = new FrameData
        {
            timestamp = _latestRgb.rgbTimestamp > _latestDepth.depthTimestamp ? _latestRgb.rgbTimestamp : _latestDepth.depthTimestamp,
            rgbFrame = _latestRgb.rgbFrame,
            depthFrame = _latestDepth.depthFrame,
            rgbTimestamp = _latestRgb.rgbTimestamp,
            depthTimestamp = _latestDepth.depthTimestamp,
            isValid = true
        };
        
        // キューに追加
        _frameQueue.Enqueue(frameData);
        // キューサイズ制限: 古いフレームを削除
        while (_frameQueue.Count > maxQueueSize)
        {
            _frameQueue.Dequeue();
        }
        
        // 処理ジョブを開始
        StartProcessingJob(frameData);
        
        // 使用済みデータをリセット
        _latestRgb.isValid = false;
        _latestDepth.isValid = false;
    }

    async void StartProcessingJob(FrameData frameData)
    {
        try
        {
            var result = await processor.ProcessAsync(frameData.rgbFrame, frameData.depthFrame);
            OnProcessingComplete(frameData.timestamp, result);
        }
        catch (Exception e)
        {
            OnProcessingError(frameData.timestamp, e.Message);
        }
    }

    void OnProcessingComplete(DateTime timestamp, RenderTexture result)
    {
        var job = new ProcessingJob
        {
            timestamp = timestamp,
            result = result,
            isCompleted = true
        };
        
        _completedJobs.Enqueue(job);
    }

    void OnProcessingError(DateTime timestamp, string error)
    {
        Debug.LogError($"Processing failed for timestamp {timestamp}: {error}");
    }

    void Update()
    {
        ProcessResultQueueOptimized();
    }

    void ProcessResultQueueOptimized()
    {
        int processedCount = 0;
        
        // 1フレームでの処理数を制限して順次処理
        while (_completedJobs.Count > 0 && processedCount < maxProcessPerFrame)
        {
            var job = _completedJobs.Dequeue();
            
            // 現在の出力よりも古い結果は破棄
            if (job.timestamp <= _latestOutputTimestamp)
            {
                // 古い結果のテクスチャを解放
                // if (autoReleaseTextures && job.result != null)
                // {
                //     RenderTexture.ReleaseTemporary(job.result);
                // }
                continue;
            }
            
            // 新しい結果を出力に反映
            ApplyProcessingResult(job);
            
            // 最新情報を更新
            _latestOutputTimestamp = job.timestamp;
            
            // FrameProviderの更新を通知
            _lastUpdateTime = DateTime.Now;
            TickUp();
            
            processedCount++;
        }
    }

    void ApplyProcessingResult(ProcessingJob job)
    {
        if (job.result != null && outputRT != null)
        {
            Graphics.CopyTexture(job.result, outputRT);
            
            // オプション: 使用済みテクスチャを解放
            // if (autoReleaseTextures)
            // {
            //     RenderTexture.ReleaseTemporary(job.result);
            // }
        }
    }

    void OnDestroy()
    {
        // イベント購読解除
        if (cameraRec != null)
            cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
        if (depthRec != null)
            depthRec.OnFrameUpdated -= OnDepthFrameReceived;
            
        // 未完了ジョブのテクスチャをクリーンアップ
        while (_completedJobs.Count > 0)
        {
            var job = _completedJobs.Dequeue();
            // if (autoReleaseTextures && job.result != null)
            // {
            //     RenderTexture.ReleaseTemporary(job.result);
            // }
        }
    }

    void OnDisable()
    {
        // イベント購読解除
        if (cameraRec != null)
            cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
        if (depthRec != null)
            depthRec.OnFrameUpdated -= OnDepthFrameReceived;
    }
}
