using UnityEngine;
using System;

public class DepthStich : FrameProvider
{
    [Header("Providers (event-driven)")]
    [SerializeField] private FrameProvider srcProvider;     // subscribe to updates (required)
    [SerializeField] private FrameProvider supportProvider; // optional

    [Header("Material")]
    [SerializeField] private Material stitchMaterial; // ImOTAR/DepthStich

    [Header("Output")]
    [SerializeField] private RenderTexture output;   // RFloat

    [SerializeField] private bool verboseLogs = false;

    private DateTime _timestamp;

    public override RenderTexture FrameTex => output;
    public override DateTime TimeStamp => _timestamp;

    private void OnEnable(){
        if (stitchMaterial == null){
            stitchMaterial = new Material(Shader.Find("ImOTAR/DepthStich"));
        }
        srcProvider.OnFrameUpdated += OnSrcUpdated;
    }

    private void OnDisable(){
        srcProvider.OnFrameUpdated -= OnSrcUpdated;
    }

    private void OnSrcUpdated(RenderTexture updatedSrc) {
        if (verboseLogs) Debug.Log($"[DepthStich] OnSrcUpdated: {updatedSrc.width}x{updatedSrc.height}, support: {supportProvider?.FrameTex?.width}x{supportProvider?.FrameTex?.height}");

        var srcRT = updatedSrc;
        var supRT = supportProvider != null ? supportProvider.FrameTex : null;
        if (srcRT == null || supRT == null) return;
        if (!srcRT.IsCreated() || !supRT.IsCreated()) return;
        if (srcRT.format != RenderTextureFormat.RFloat || supRT.format != RenderTextureFormat.RFloat) return;

        // Align output size to src. Sampling uses normalized UV so support will be resampled.
        EnsureOutput(srcRT.width, srcRT.height);

        stitchMaterial.SetTexture("_Src", srcRT);
        stitchMaterial.SetTexture("_Support", supRT);
        Graphics.Blit(null, output, stitchMaterial, 0);

        if (!IsInitTexture)
        {
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
        _timestamp = DateTime.Now;
        TickUp();
        
        if (verboseLogs) Debug.Log($"[DepthStich] OnSrcUpdated: output {output.width}x{output.height}");
    }

    private void EnsureOutput(int w, int h){
        if (output != null && output.width == w && output.height == h && output.format == RenderTextureFormat.RFloat) return;
        if (output != null){
            if (output.IsCreated()) output.Release();
            DestroyImmediate(output);
        }
        output = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear){
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point,
            useMipMap = false,
            autoGenerateMips = false
        };
        output.Create();
    }
}

