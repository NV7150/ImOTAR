using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public sealed class StichApplyProvider : FrameProvider
{
    private const string KERNEL_NAME = "CSMain";

    // Shader property IDs
    private static readonly int PropStatic = Shader.PropertyToID("_Static");
    private static readonly int PropDynamic = Shader.PropertyToID("_Dynamic");
    private static readonly int PropMask = Shader.PropertyToID("_Mask");
    private static readonly int PropOutput = Shader.PropertyToID("output");
    private static readonly int PropStaticSize = Shader.PropertyToID("_StaticSize");
    private static readonly int PropDynamicSize = Shader.PropertyToID("_DynamicSize");
    private static readonly int PropStaticCode = Shader.PropertyToID("_StaticCode");
    private static readonly int PropDynamicCode = Shader.PropertyToID("_DynamicCode");

    [Header("Providers (event-driven)")]
    [SerializeField] private FrameProvider staticSrc;
    [SerializeField] private FrameProvider dynamicSrc;
    [SerializeField] private FrameProvider maskSrc; // RInt codes

    [Header("Mask codes (int)")]
    [SerializeField] private int staticCode = 1;
    [SerializeField] private int dynamicCode = 2;

    [Header("Shader/Output")]
    [SerializeField] private ComputeShader shader;  // Assets/Shaders/StichApply.compute
    [SerializeField] private RenderTexture output;  // RFloat meters; external resource, Create() here

    private int kernel;
    private DateTime lastTimestamp;
    private bool isReady;

    public override RenderTexture FrameTex => output;
    public override DateTime TimeStamp => lastTimestamp;

    private void OnEnable()
    {
        ValidateSerialized();
        kernel = shader.FindKernel(KERNEL_NAME);
        if (kernel < 0) throw new InvalidOperationException($"Kernel '{KERNEL_NAME}' not found in shader {shader.name}");

        staticSrc.OnFrameTexInit += OnAnyInit;
        dynamicSrc.OnFrameTexInit += OnAnyInit;
        maskSrc.OnFrameTexInit += OnAnyInit;
        staticSrc.OnFrameUpdated += OnAnyUpdated;
        dynamicSrc.OnFrameUpdated += OnAnyUpdated;
        maskSrc.OnFrameUpdated += OnAnyUpdated;

        TryEnsureOutput();
    }

    private void OnDisable()
    {
        if (staticSrc != null)
        {
            staticSrc.OnFrameTexInit -= OnAnyInit;
            staticSrc.OnFrameUpdated -= OnAnyUpdated;
        }
        if (dynamicSrc != null)
        {
            dynamicSrc.OnFrameTexInit -= OnAnyInit;
            dynamicSrc.OnFrameUpdated -= OnAnyUpdated;
        }
        if (maskSrc != null)
        {
            maskSrc.OnFrameTexInit -= OnAnyInit;
            maskSrc.OnFrameUpdated -= OnAnyUpdated;
        }
    }

    private void ValidateSerialized()
    {
        if (staticSrc == null) throw new InvalidOperationException("staticSrc is not assigned");
        if (dynamicSrc == null) throw new InvalidOperationException("dynamicSrc is not assigned");
        if (maskSrc == null) throw new InvalidOperationException("maskSrc is not assigned");
        if (shader == null) throw new InvalidOperationException("ComputeShader is not assigned");
        if (output == null) throw new InvalidOperationException("output RenderTexture must be assigned via Inspector");
        if (staticCode == dynamicCode) throw new InvalidOperationException("staticCode must differ from dynamicCode");
    }

    private void OnAnyInit(RenderTexture _)
    {
        TryEnsureOutput();
    }

    private void OnAnyUpdated(RenderTexture _)
    {
        TryDispatch();
    }

    private void TryEnsureOutput()
    {
        var sTex = staticSrc.FrameTex;
        var dTex = dynamicSrc.FrameTex;
        var mTex = maskSrc.FrameTex;
        if (sTex == null || dTex == null || mTex == null) return;
        if (!sTex.IsCreated() || !dTex.IsCreated() || !mTex.IsCreated()) return;

        EnsureInputFormatsOrThrow(sTex, dTex, mTex);
        EnsureOutputOrThrow(sTex, mTex);

        if (!IsInitTexture && output.IsCreated())
        {
            OnFrameTexInitialized();
            IsInitTexture = true;
        }

        isReady = true;
    }

    private static void EnsureInputFormatsOrThrow(RenderTexture sTex, RenderTexture dTex, RenderTexture mTex)
    {
        if (sTex.graphicsFormat != GraphicsFormat.R32_SFloat)
            throw new InvalidOperationException("Static RenderTexture must be R32_SFloat (RFloat)");
        if (dTex.graphicsFormat != GraphicsFormat.R32_SFloat)
            throw new InvalidOperationException("Dynamic RenderTexture must be R32_SFloat (RFloat)");
        if (mTex.graphicsFormat != GraphicsFormat.R32_SInt)
            throw new InvalidOperationException("Mask RenderTexture must be R32_SInt (RInt)");
    }

    private void EnsureOutputOrThrow(RenderTexture sTex, RenderTexture mTex)
    {
        if (output.graphicsFormat != GraphicsFormat.R32_SFloat)
            throw new InvalidOperationException("Output RenderTexture must be R32_SFloat (RFloat)");
        if (!output.enableRandomWrite)
            throw new InvalidOperationException("Output RenderTexture must have enableRandomWrite=true");
        if (output.width != sTex.width || output.height != sTex.height)
            throw new InvalidOperationException("Output size must match staticSrc size");
        if (mTex.width != sTex.width || mTex.height != sTex.height)
            throw new InvalidOperationException("Mask size must match staticSrc size");
        if (!output.IsCreated()) output.Create();
    }

    private void TryDispatch()
    {
        if (!isReady) { TryEnsureOutput(); if (!isReady) return; }

        var sTex = staticSrc.FrameTex;
        var dTex = dynamicSrc.FrameTex;
        var mTex = maskSrc.FrameTex;
        if (sTex == null || dTex == null || mTex == null) return;
        if (!sTex.IsCreated() || !dTex.IsCreated() || !mTex.IsCreated()) return;

        var prevFilterDyn = dTex.filterMode;
        dTex.filterMode = FilterMode.Bilinear; // for UV bilinear sampling

        shader.SetTexture(kernel, PropStatic, sTex);
        shader.SetTexture(kernel, PropDynamic, dTex);
        shader.SetTexture(kernel, PropMask, mTex);
        shader.SetTexture(kernel, PropOutput, output);
        shader.SetInts(PropStaticSize, sTex.width, sTex.height);
        shader.SetInts(PropDynamicSize, dTex.width, dTex.height);
        shader.SetInt(PropStaticCode, staticCode);
        shader.SetInt(PropDynamicCode, dynamicCode);

        int tgx = Mathf.CeilToInt(sTex.width / 16.0f);
        int tgy = Mathf.CeilToInt(sTex.height / 16.0f);
        shader.Dispatch(kernel, tgx, tgy, 1);

        dTex.filterMode = prevFilterDyn; // restore

        // timestamp = max of three inputs
        var tsS = staticSrc.TimeStamp;
        var tsD = dynamicSrc.TimeStamp;
        var tsM = maskSrc.TimeStamp;
        var tsMax = (tsS >= tsD) ? tsS : tsD;
        lastTimestamp = (tsMax >= tsM) ? tsMax : tsM;
        TickUp();
    }
}


