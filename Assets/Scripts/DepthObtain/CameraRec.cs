using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

public class CameraRec : FrameProvider
{
    [SerializeField] private ARCameraManager camManager;
    [SerializeField] private ARCameraBackground arCameraBackground;
    [SerializeField] private RenderTexture targetRT;
    private CommandBuffer cmd;
    private DateTime lastUpdateTime;
    
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    
    // FrameProviderの抽象プロパティを実装
    public override RenderTexture FrameTex => targetRT;
    public override DateTime TimeStamp => lastUpdateTime;

    void Awake()
    {
        // 初期化時にテクスチャが設定されていることを通知
        if (targetRT != null)
        {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
    }

    void OnEnable()
    {
        if (camManager == null || arCameraBackground == null || targetRT == null)
        {
            Debug.LogWarning("ARCameraManager / ARCameraBackground / targetRT の参照を確認してください。");
            enabled = false;
            return;
        }
        camManager.frameReceived += OnFrameReceived;
    }

    void OnDisable()
    {
        if (camManager != null)
            camManager.frameReceived -= OnFrameReceived;

        if (cmd != null)
        {
            cmd.Release();
            cmd = null;
        }
    }

    // 公式手順：frameReceived の“そのフレーム中”に即時 Blit（外部テクスチャはフレーム境界を跨がない）
    private void OnFrameReceived(ARCameraFrameEventArgs args)
    {
        if (targetRT == null) return;

        var mat = arCameraBackground.material; 
        if (mat == null) return;

        Texture src = mat.HasProperty(MainTexId) ? mat.GetTexture(MainTexId) : null;

        if (cmd == null) cmd = new CommandBuffer { name = "AR Camera Background → RT (immediate blit)" };
        else cmd.Clear();
    // Make the CB self-contained: set target and blit directly to targetRT.
    cmd.SetRenderTarget(targetRT);
    cmd.ClearRenderTarget(clearDepth: true, clearColor: false, backgroundColor: Color.clear);
    cmd.Blit(src, targetRT, mat);
    Graphics.ExecuteCommandBuffer(cmd);
        
        // タイムスタンプを更新してティックアップ
        lastUpdateTime = DateTime.Now;
        TickUp();
    }
}
