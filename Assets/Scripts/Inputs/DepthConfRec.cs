// Unity 2020 LTS / 2021 LTS + AR Foundation 4.1/4.2 + ARKit XR Plugin 4.x 想定
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

public class DepthConfRec : FrameProvider
{
    [Header("AR Occlusion Manager を割当て")]
    [SerializeField] private AROcclusionManager occlusion;

    [Header("出力先 RenderTexture を割当て")]
    [SerializeField] private RenderTexture targetRT;

    [Header("Confidence正規化用 Compute Shader (iOS専用)")]
    [SerializeField] private ComputeShader confidenceNormalizeCS;

    private CommandBuffer cmd;
    private Material flipMaterial;
    private RenderTexture tempRT; // Compute Shader出力用の一時RT
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
#if UNITY_IOS
        if (occlusion == null || targetRT == null || confidenceNormalizeCS == null)
        {
            Debug.LogWarning("AROcclusionManager / targetRT / confidenceNormalizeCS の参照を確認してください。");
            enabled = false;
            return;
        }

        // 一時RTを作成（Compute Shader出力用）
        if (tempRT == null)
        {
            tempRT = new RenderTexture(targetRT.width, targetRT.height, 0, RenderTextureFormat.ARGBFloat);
            tempRT.enableRandomWrite = true;
            tempRT.Create();
        }

        // 必要なら Inspector で Environment Depth Mode を Fastest/Medium/Best に設定しておく
        // occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Best;

        occlusion.frameReceived += OnOcclusionFrame;
#else
        throw new System.NotSupportedException("DepthConfRec with confidence normalization is only supported on iOS (UNITY_IOS).");
#endif
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

        // 一時RTをクリーンアップ
        if (tempRT != null)
        {
            tempRT.Release();
            tempRT = null;
        }
    }

    // Occlusion フレーム毎に GPU テクスチャを RT へ即時コピー
    private void OnOcclusionFrame(AROcclusionFrameEventArgs args)
    {
#if UNITY_IOS
        if (targetRT == null || tempRT == null) return;

        // AF 6.1+: TryGetEnvironmentDepthConfidenceTexture()を使用
        if (!occlusion.TryGetEnvironmentDepthConfidenceTexture(out var depthConfTexRaw))
        {
            return;
        }

        var depthConfTex = depthConfTexRaw.texture;

        // Step 1: Compute Shader で Confidence を正規化 (0/1/2 → 0/0.5/1)
        int kernelIndex = confidenceNormalizeCS.FindKernel("CSMain");
        confidenceNormalizeCS.SetTexture(kernelIndex, "_SourceTex", depthConfTex);
        confidenceNormalizeCS.SetTexture(kernelIndex, "_Result", tempRT);
        confidenceNormalizeCS.SetInt("_SourceWidth", depthConfTex.width);
        confidenceNormalizeCS.SetInt("_SourceHeight", depthConfTex.height);
        confidenceNormalizeCS.SetInt("_TargetWidth", tempRT.width);
        confidenceNormalizeCS.SetInt("_TargetHeight", tempRT.height);
        
        // Dispatch compute shader
        int threadGroupsX = Mathf.CeilToInt(tempRT.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(tempRT.height / 8.0f);
        confidenceNormalizeCS.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, 1);

        // Step 2: Flipper で反転処理して targetRT へ
        if (cmd == null) cmd = new CommandBuffer { name = "ARKit EnvironmentDepthConfidence → RT (normalize + flip)" };
        else cmd.Clear();

        cmd.SetRenderTarget(targetRT);
        cmd.ClearRenderTarget(clearDepth: true, clearColor: false, backgroundColor: Color.clear);
        cmd.Blit(tempRT, targetRT, flipMaterial);
        Graphics.ExecuteCommandBuffer(cmd);

        // タイムスタンプを更新してティックアップ
        lastUpdateTime = DateTime.Now;
        TickUp();
#else
        // iOS以外では実行されない（OnEnableで例外投げるため）
#endif
    }
}
