// Unity 2020 LTS / 2021 LTS + AR Foundation 4.1/4.2 + ARKit XR Plugin 4.x 想定
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

public class DepthRec : FrameProvider
{
    [Header("AR Occlusion Manager を割当て")]
    [SerializeField] private AROcclusionManager occlusion;

    [Header("出力先 RenderTexture を割当て")]
    [SerializeField] private RenderTexture targetRT;

    private CommandBuffer cmd;
    private Material flipMaterial;
    private DateTime lastUpdateTime;

    // FrameProviderの抽象プロパティを実装
    public override RenderTexture FrameTex => targetRT;
    public override DateTime TimeStamp => lastUpdateTime;



    void Start()
    {
        // Flipper シェーダーを使用するマテリアルを動的作成
        var shader = Shader.Find("ImOTAR/Flipper");
        if (shader == null)
        {
            Debug.LogError("ImOTAR/Flipper シェーダーが見つかりません。");
            enabled = false;
            return;
        }
        
        flipMaterial = new Material(shader);
        
        // 初期化時にテクスチャが設定されていることを通知
        if (targetRT != null)
        {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
    }

    void OnEnable()
    {
        if (occlusion == null || targetRT == null)
        {
            Debug.LogWarning("AROcclusionManager / targetRT の参照を確認してください。");
            enabled = false;
            return;
        }

        // 必要なら Inspector で Environment Depth Mode を Fastest/Medium/Best に設定しておく
        // occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Best;

        occlusion.frameReceived += OnOcclusionFrame;
    }

    void OnDisable()
    {
        if (occlusion != null)
            occlusion.frameReceived -= OnOcclusionFrame;

        if (cmd != null)
        {
            cmd.Release();
            cmd = null;
        }

        // 動的作成したマテリアルをクリーンアップ
        if (flipMaterial != null)
        {
            DestroyImmediate(flipMaterial);
            flipMaterial = null;
        }
    }

    // Occlusion フレーム毎に GPU テクスチャを RT へ即時コピー
    private void OnOcclusionFrame(AROcclusionFrameEventArgs args)
    {
        if (targetRT == null) return;

        // AF 6.1+: TryGetEnvironmentDepthTexture()を使用
        if (!occlusion.TryGetEnvironmentDepthTexture(out var depthTex))
        {
            return;
        }

        if (cmd == null) cmd = new CommandBuffer { name = "ARKit EnvironmentDepth → RT (immediate blit)" };
        else cmd.Clear();

        // Make the CB self-contained: set target and blit directly to targetRT.
        cmd.SetRenderTarget(targetRT);
        cmd.ClearRenderTarget(clearDepth: true, clearColor: false, backgroundColor: Color.clear);
        // depthTex は GPU テクスチャ（外部テクスチャの場合あり）。明示的に targetRT へ Blit
        cmd.Blit(depthTex, targetRT, flipMaterial);
        Graphics.ExecuteCommandBuffer(cmd);

        // タイムスタンプを更新してティックアップ
        lastUpdateTime = DateTime.Now;
        TickUp();
    }
}
