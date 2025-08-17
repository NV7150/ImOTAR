using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// テスト用の深度フレーム配信モック
/// SerializedFieldで設定したTexture2Dを指定間隔でRenderTextureに変換して配信
/// </summary>
public class PseudoDepthRec : FrameProvider
{
    [Header("Test Input")]
    [SerializeField] private Texture2D testDepthTexture; // R16, mm単位
    
    [Header("Output")]
    [SerializeField] private RenderTexture targetRT; // RFloat, meters単位
    
    [Header("Conversion")]
    [SerializeField] private Material depthConversionMaterial; // R16mm → meters変換用
    
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
        // 深度変換マテリアルの設定を確認
        ValidateDepthConversionMaterial();
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
        if (testDepthTexture == null)
        {
            Debug.LogWarning($"[{name}] testDepthTexture が設定されていません");
            return false;
        }
        
        if (targetRT == null)
        {
            Debug.LogWarning($"[{name}] targetRT が設定されていません");
            return false;
        }
        
        if (depthConversionMaterial == null)
        {
            Debug.LogWarning($"[{name}] depthConversionMaterial が設定されていません");
            return false;
        }
        
        return true;
    }

    private void ValidateDepthConversionMaterial()
    {
        if (depthConversionMaterial != null && depthConversionMaterial.shader != null)
        {
            if (!depthConversionMaterial.shader.name.Contains("DepthR16ToMeters"))
            {
                Debug.LogWarning($"[{name}] depthConversionMaterial に適切なシェーダーが設定されていません");
            }
        }
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
        if (testDepthTexture == null || targetRT == null || depthConversionMaterial == null) return;

        // R16 mm単位 → RFloat meters単位に変換
        Graphics.Blit(testDepthTexture, targetRT, depthConversionMaterial);
        
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
