using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// テスト用のカメラフレーム配信モック
/// SerializedFieldで設定したTexture2Dを指定間隔でRenderTextureに変換して配信
/// </summary>
public class PseudoCamRec : FrameProvider
{
    [Header("Test Input")]
    [SerializeField] private Texture2D testImageTexture;
    
    [Header("Output")]
    [SerializeField] private RenderTexture targetRT;
    
    [Header("Timing Settings")]
    [SerializeField] private float intervalMs = 33.33f; // ~30fps
    [SerializeField] private bool autoStart = true;
    [SerializeField] private bool loop = true;
    
    private DateTime lastUpdateTime;
    private Coroutine sendCoroutine;
    
    // FrameProviderの抽象プロパティを実装
    public override RenderTexture FrameTex => targetRT;
    public override DateTime TimeStamp => lastUpdateTime;

    void Awake()
    {
        // マテリアル不要：直接Blitする
    }

    void Start()
    {
        ValidateConfiguration();
        
        // 初期化時にテクスチャが設定されていることを通知
        if (targetRT != null)
        {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
        
        if (autoStart)
        {
            StartSending();
        }
    }

    void OnEnable()
    {
        if (autoStart && sendCoroutine == null)
        {
            StartSending();
        }
    }

    void OnDisable()
    {
        StopSending();
    }

    void OnDestroy()
    {
        StopSending();
    }

    public void StartSending()
    {
        if (!ValidateConfiguration()) return;
        
        StopSending(); // 既存のCoroutineを停止
        sendCoroutine = StartCoroutine(SendFrameCoroutine());
    }

    public void StopSending()
    {
        if (sendCoroutine != null)
        {
            StopCoroutine(sendCoroutine);
            sendCoroutine = null;
        }
    }

    public void SendSingleFrame()
    {
        if (!ValidateConfiguration()) return;
        
        SendFrame();
    }

    private bool ValidateConfiguration()
    {
        if (testImageTexture == null)
        {
            Debug.LogWarning($"[{name}] testImageTexture が設定されていません");
            return false;
        }
        
        if (targetRT == null)
        {
            Debug.LogWarning($"[{name}] targetRT が設定されていません");
            return false;
        }
        
        return true;
    }

    private IEnumerator SendFrameCoroutine()
    {
        var wait = new WaitForSeconds(intervalMs / 1000f);
        
        do
        {
            SendFrame();
            yield return wait;
        }
        while (loop);
    }

    private void SendFrame()
    {
        if (testImageTexture == null || targetRT == null) return;

        // Texture2DをRenderTextureに直接コピー
        Graphics.Blit(testImageTexture, targetRT);
        
        // タイムスタンプを更新してフレーム更新を通知
        lastUpdateTime = DateTime.Now;
        TickUp();
    }

    // Inspector用のテストボタン
    [ContextMenu("Send Single Frame")]
    void TestSendFrame()
    {
        SendSingleFrame();
    }

    [ContextMenu("Start Sending")]
    void TestStartSending()
    {
        StartSending();
    }

    [ContextMenu("Stop Sending")]
    void TestStopSending()
    {
        StopSending();
    }
}
