using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[DisallowMultipleComponent]
public sealed class DepthMixProvider : FrameProvider
{
    private const string KERNEL_NAME = "CSMain";

    // Props
    private static readonly int PropStatic = Shader.PropertyToID("_Static");
    private static readonly int PropDynamic = Shader.PropertyToID("_Dynamic");
    private static readonly int PropConfidence = Shader.PropertyToID("_Confidence");
    private static readonly int PropSearchMask = Shader.PropertyToID("_SearchMask");
    private static readonly int PropEdgeMask = Shader.PropertyToID("_EdgeMask");

    private static readonly int PropOutput = Shader.PropertyToID("output");
    private static readonly int PropDebug = Shader.PropertyToID("debugMask");

    private static readonly int PropOutSize = Shader.PropertyToID("_OutSize");
    private static readonly int PropDynSize = Shader.PropertyToID("_DynSize");
    private static readonly int PropConfSize = Shader.PropertyToID("_ConfSize");

    private static readonly int PropConfTh = Shader.PropertyToID("_ConfTh");
    private static readonly int PropInvalidVal = Shader.PropertyToID("_InvalidVal");

    [Header("Inputs")]
    [SerializeField] private FrameProvider staticProvider;
    [SerializeField] private FrameProvider dynamicProvider;
    [SerializeField] private FrameProvider confidenceProvider;
    [SerializeField] private FrameProvider searchMaskProvider; // R32_SInt (-1/0/1)
    [SerializeField] private FrameProvider edgeMaskProvider;   // R32_SInt 0/1 (dilated)

    [Header("Params")]
    [SerializeField, Range(0f, 1f)] private float confTh = 0.6f;
    [SerializeField] private float invalidValue = -1.0f;

    [Header("Shader/Output")]
    [SerializeField] private ComputeShader shader;   // Assets/Shaders/Mixer/DepthMix.compute
    [SerializeField] private RenderTexture output;   // RFloat
    [SerializeField] private RenderTexture debugMask; // R32_SInt

    private int kernel;
    private uint tgx = 8, tgy = 8, tgz = 1;
    private bool ready;
    private DateTime lastTs;

    public override RenderTexture FrameTex => output;
    public override DateTime TimeStamp => lastTs;

    private void OnEnable()
    {
        ValidateSerialized();
        kernel = shader.FindKernel(KERNEL_NAME);
        if (kernel < 0) throw new InvalidOperationException($"Kernel '{KERNEL_NAME}' not found in {shader.name}");
        shader.GetKernelThreadGroupSizes(kernel, out tgx, out tgy, out tgz);

        Subscribe();
        TryEnsureOutputs();
    }

    private void OnDisable()
    {
        Unsubscribe();
        ready = false;
        IsInitTexture = false;
    }

    private void OnValidate()
    {
        if (invalidValue >= 0f) invalidValue = -1f; // enforce negative invalid
        confTh = Mathf.Clamp01(confTh);
    }

    private void Subscribe()
    {
        SubscribeOne(staticProvider);
        SubscribeOne(dynamicProvider);
        SubscribeOne(confidenceProvider);
        SubscribeOne(searchMaskProvider);
        SubscribeOne(edgeMaskProvider);
    }

    private void SubscribeOne(FrameProvider p)
    {
        if (p == null) return;
        p.OnFrameTexInit += OnAnyInit;
        p.OnFrameUpdated += OnAnyUpdated;
    }

    private void Unsubscribe()
    {
        UnsubscribeOne(staticProvider);
        UnsubscribeOne(dynamicProvider);
        UnsubscribeOne(confidenceProvider);
        UnsubscribeOne(searchMaskProvider);
        UnsubscribeOne(edgeMaskProvider);
    }

    private void UnsubscribeOne(FrameProvider p)
    {
        if (p == null) return;
        p.OnFrameTexInit -= OnAnyInit;
        p.OnFrameUpdated -= OnAnyUpdated;
    }

    private void OnAnyInit(RenderTexture _)
    {
        TryEnsureOutputs();
    }

    private void OnAnyUpdated(RenderTexture _)
    {
        TryDispatch();
    }

    private void ValidateSerialized()
    {
        if (staticProvider == null) throw new InvalidOperationException("staticProvider is not assigned");
        if (dynamicProvider == null) throw new InvalidOperationException("dynamicProvider is not assigned");
        if (confidenceProvider == null) throw new InvalidOperationException("confidenceProvider is not assigned");
        if (searchMaskProvider == null) throw new InvalidOperationException("searchMaskProvider is not assigned");
        if (edgeMaskProvider == null) throw new InvalidOperationException("edgeMaskProvider is not assigned");
        if (shader == null) throw new InvalidOperationException("ComputeShader is not assigned");
        if (output == null) throw new InvalidOperationException("output RenderTexture must be assigned via Inspector");
        if (debugMask == null) throw new InvalidOperationException("debugMask RenderTexture must be assigned via Inspector");
    }

    private void TryEnsureOutputs()
    {
        var sTex = staticProvider.FrameTex;
        if (sTex == null || !sTex.IsCreated()) return;

        EnsureOutputOrThrow(sTex);
        EnsureDebugOrThrow(sTex);

        if (!IsInitTexture && output.IsCreated() && debugMask.IsCreated())
        {
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
        ready = true;
    }

    private void EnsureOutputOrThrow(RenderTexture sTex)
    {
        if (output.graphicsFormat != GraphicsFormat.R32_SFloat && output.format != RenderTextureFormat.RFloat)
            throw new InvalidOperationException("output must be RFloat");
        if (!output.enableRandomWrite)
            throw new InvalidOperationException("output must have enableRandomWrite=true");
        if (output.width != sTex.width || output.height != sTex.height)
            throw new InvalidOperationException("output size must match staticProvider size");
        if (!output.IsCreated()) output.Create();
    }

    private void EnsureDebugOrThrow(RenderTexture sTex)
    {
        if (debugMask.graphicsFormat != GraphicsFormat.R32_SInt)
            throw new InvalidOperationException("debugMask must be R32_SInt (RInt)");
        if (!debugMask.enableRandomWrite)
            throw new InvalidOperationException("debugMask must have enableRandomWrite=true");
        if (debugMask.width != sTex.width || debugMask.height != sTex.height)
            throw new InvalidOperationException("debugMask size must match staticProvider size");
        if (!debugMask.IsCreated()) debugMask.Create();
    }

    private void TryDispatch()
    {
        if (!ready) { TryEnsureOutputs(); if (!ready) return; }

        var sTex = staticProvider.FrameTex;
        var dTex = dynamicProvider.FrameTex;
        var cTex = confidenceProvider.FrameTex;
        var smTex = searchMaskProvider.FrameTex;
        var emTex = edgeMaskProvider.FrameTex;

        if (sTex == null || dTex == null || cTex == null || smTex == null || emTex == null) return;
        if (!sTex.IsCreated() || !dTex.IsCreated() || !cTex.IsCreated() || !smTex.IsCreated() || !emTex.IsCreated()) return;

        // Enforce sampling config for resampling inputs
        dTex.wrapMode = TextureWrapMode.Clamp; dTex.filterMode = FilterMode.Bilinear;
        cTex.wrapMode = TextureWrapMode.Clamp; cTex.filterMode = FilterMode.Bilinear;
        // invalid mask is integer mask; no filtering

        // Masks must be integer R32_SInt and same size as static
        if (smTex.graphicsFormat != GraphicsFormat.R32_SInt)
            throw new InvalidOperationException("searchMask must be R32_SInt (-1/0/1)");
        if (emTex.graphicsFormat != GraphicsFormat.R32_SInt)
            throw new InvalidOperationException("edgeMask must be R32_SInt (0/1)");
        if (smTex.width != sTex.width || smTex.height != sTex.height)
            throw new InvalidOperationException("searchMask size must match staticProvider size");
        if (emTex.width != sTex.width || emTex.height != sTex.height)
            throw new InvalidOperationException("edgeMask size must match staticProvider size");

        shader.SetTexture(kernel, PropStatic, sTex);
        shader.SetTexture(kernel, PropDynamic, dTex);
        shader.SetTexture(kernel, PropConfidence, cTex);
        shader.SetTexture(kernel, PropSearchMask, smTex);
        shader.SetTexture(kernel, PropEdgeMask, emTex);

        shader.SetTexture(kernel, PropOutput, output);
        shader.SetTexture(kernel, PropDebug, debugMask);

        shader.SetInts(PropOutSize, sTex.width, sTex.height);
        shader.SetInts(PropDynSize, dTex.width, dTex.height);
        shader.SetInts(PropConfSize, cTex.width, cTex.height);

        shader.SetFloat(PropConfTh, confTh);
        shader.SetFloat(PropInvalidVal, invalidValue);

        int gx = Mathf.CeilToInt(sTex.width / (float)tgx);
        int gy = Mathf.CeilToInt(sTex.height / (float)tgy);
        shader.Dispatch(kernel, gx, gy, 1);

        // Timestamp: latest among inputs
        lastTs = staticProvider.TimeStamp;
        if (dynamicProvider.TimeStamp > lastTs) lastTs = dynamicProvider.TimeStamp;
        if (confidenceProvider.TimeStamp > lastTs) lastTs = confidenceProvider.TimeStamp;
        if (searchMaskProvider.TimeStamp > lastTs) lastTs = searchMaskProvider.TimeStamp;
        if (edgeMaskProvider.TimeStamp > lastTs) lastTs = edgeMaskProvider.TimeStamp;

        TickUp();
    }
}


