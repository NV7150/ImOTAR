using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[DisallowMultipleComponent]
public sealed class DiffMaskProvider : FrameProvider
{
    private const string KERNEL_NAME = "CSMain";

    private static readonly int PropDiff = Shader.PropertyToID("_Diff");
    private static readonly int PropOutput = Shader.PropertyToID("output");
    private static readonly int PropOutSize = Shader.PropertyToID("_OutSize");
    private static readonly int PropDiffSize = Shader.PropertyToID("_DiffSize");
    private static readonly int PropDiffTh = Shader.PropertyToID("_DiffTh");

    [Header("Inputs")]
    [SerializeField] private FrameProvider diff;

    [Header("Params")]
    [SerializeField] private float diffTh = 0.05f;

    [Header("Shader/Output")]
    [SerializeField] private ComputeShader shader;   // Assets/Shaders/Mixer/DiffMask.compute
    [SerializeField] private RenderTexture output;   // R32_SInt, values in {-1,0,1}

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
        TryEnsureOutput();
    }

    private void OnDisable()
    {
        Unsubscribe();
        ready = false;
        IsInitTexture = false;
    }

    private void OnValidate()
    {
        if (diffTh < 0f) diffTh = 0f;
    }

    private void Subscribe()
    {
        if (diff != null)
        {
            diff.OnFrameTexInit += OnAnyInit;
            diff.OnFrameUpdated += OnAnyUpdated;
        }
        
    }

    private void Unsubscribe()
    {
        if (diff != null)
        {
            diff.OnFrameTexInit -= OnAnyInit;
            diff.OnFrameUpdated -= OnAnyUpdated;
        }
        
    }

    private void OnAnyInit(RenderTexture _)
    {
        TryEnsureOutput();
    }

    private void OnAnyUpdated(RenderTexture _)
    {
        TryDispatch();
    }

    private void ValidateSerialized()
    {
        if (diff == null) throw new InvalidOperationException("diff provider is not assigned");
        if (shader == null) throw new InvalidOperationException("ComputeShader is not assigned");
        if (output == null) throw new InvalidOperationException("output RenderTexture must be assigned via Inspector");
    }

    private void TryEnsureOutput()
    {
        EnsureOutputOrThrow();

        if (!IsInitTexture && output.IsCreated())
        {
            OnFrameTexInitialized();
            IsInitTexture = true;
        }

        ready = true;
    }

    private void EnsureOutputOrThrow()
    {
        if (output.graphicsFormat != GraphicsFormat.R32_SInt)
            throw new InvalidOperationException("Output RenderTexture must be R32_SInt (RInt)");
        if (!output.enableRandomWrite)
            throw new InvalidOperationException("Output RenderTexture must have enableRandomWrite=true");
        if (output.width <= 0 || output.height <= 0)
            throw new InvalidOperationException("Output size must be positive");
        if (!output.IsCreated()) output.Create();
    }

    private void TryDispatch()
    {
        if (!ready) { TryEnsureOutput(); if (!ready) return; }

        var dTex = diff.FrameTex;
        if (output == null || dTex == null) return;
        if (!output.IsCreated() || !dTex.IsCreated()) return;

        // Enforce bilinear + clamp sampling for resampling correctness
        dTex.wrapMode = TextureWrapMode.Clamp;
        dTex.filterMode = FilterMode.Bilinear;

        shader.SetTexture(kernel, PropDiff, dTex);
        shader.SetTexture(kernel, PropOutput, output);
        shader.SetInts(PropOutSize, output.width, output.height);
        shader.SetInts(PropDiffSize, dTex.width, dTex.height);
        shader.SetFloat(PropDiffTh, diffTh);

        int gx = Mathf.CeilToInt(output.width / (float)tgx);
        int gy = Mathf.CeilToInt(output.height / (float)tgy);
        shader.Dispatch(kernel, gx, gy, 1);

        lastTs = diff.TimeStamp;
        TickUp();
    }
}


