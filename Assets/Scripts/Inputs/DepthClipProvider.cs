using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public class DepthClipProvider : FrameProvider
{
    [Header("Input FrameProvider")]
    [SerializeField] private FrameProvider src;

    [Header("Output RenderTexture (assign externally)")]
    [SerializeField] private RenderTexture targetRT;

    [Header("Material (ImOTAR/DepthClip)")]
    [SerializeField] private Material material;

    [Header("Clip distance (meters)")]
    [SerializeField] private float clipDist = 5.0f;

    [Header("Clip epsilon (meters)")]
    [SerializeField] private float clipEps = 0.0f;

    private DateTime lastTime;

    public override RenderTexture FrameTex => targetRT;
    public override DateTime TimeStamp => lastTime;

    public float ClipDist
    {
        get => clipDist;
        set
        {
            if (value <= 0f) throw new ArgumentOutOfRangeException(nameof(ClipDist), "clipDist must be > 0");
            clipDist = value;
        }
    }

    public float ClipEps
    {
        get => clipEps;
        set
        {
            if (value < 0f) throw new ArgumentOutOfRangeException(nameof(ClipEps), "clipEps must be >= 0");
            clipEps = value;
        }
    }

    void Start()
    {
        if (targetRT != null)
        {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
    }

    void OnEnable()
    {
        if (src == null) throw new InvalidOperationException("DepthClipProvider: src is null");
        if (targetRT == null) throw new InvalidOperationException("DepthClipProvider: targetRT is null");
        if (material == null) throw new InvalidOperationException("DepthClipProvider: material is null");
        if (!material.HasProperty("_ClipDist") || !material.HasProperty("_ClipEps"))
            throw new InvalidOperationException("DepthClipProvider: material missing _ClipDist/_ClipEps properties");
        if (clipDist <= 0f) throw new InvalidOperationException("DepthClipProvider: clipDist must be > 0");

        src.OnFrameTexInit += OnSrcInit;
        src.OnFrameUpdated += OnSrcUpdated;
    }

    void OnDisable()
    {
        if (src != null)
        {
            src.OnFrameTexInit -= OnSrcInit;
            src.OnFrameUpdated -= OnSrcUpdated;
        }
    }

    private void OnSrcInit(RenderTexture _)
    {
        ValidateResources();
    }

    private void OnSrcUpdated(RenderTexture _)
    {
        ValidateResources();
        Dispatch();
        lastTime = src.TimeStamp;
        TickUp();
    }

    private void ValidateResources()
    {
        if (src.FrameTex == null) throw new InvalidOperationException("DepthClipProvider: src.FrameTex is null");
        if (targetRT == null) throw new InvalidOperationException("DepthClipProvider: targetRT is null");

        if (src.FrameTex.width != targetRT.width || src.FrameTex.height != targetRT.height)
            throw new InvalidOperationException("DepthClipProvider: size mismatch between src.FrameTex and targetRT");

        var inGF = src.FrameTex.graphicsFormat;
        if (!(inGF == GraphicsFormat.R32_SFloat || inGF == GraphicsFormat.R16_SFloat))
            throw new InvalidOperationException("DepthClipProvider: src.FrameTex must be SFloat (R16/R32) absolute meters");

        var outGF = targetRT.graphicsFormat;
        if (!(outGF == GraphicsFormat.R32_SFloat || outGF == GraphicsFormat.R16_SFloat))
            throw new InvalidOperationException("DepthClipProvider: targetRT must be SFloat (R16/R32)");
    }

    private void Dispatch()
    {
        material.SetFloat("_ClipDist", clipDist);
        material.SetFloat("_ClipEps", clipEps);
        Graphics.Blit(src.FrameTex, targetRT, material);
    }
}
