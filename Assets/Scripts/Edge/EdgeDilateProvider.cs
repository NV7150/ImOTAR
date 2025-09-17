using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[DisallowMultipleComponent]
public sealed class EdgeDilateProvider : FrameProvider
{
    private const string KERNEL_NAME = "CSMain";

    private static readonly int PropInput = Shader.PropertyToID("_Input");
    private static readonly int PropOutput = Shader.PropertyToID("output");
    private static readonly int PropOutSize = Shader.PropertyToID("_OutSize");
    private static readonly int PropRadius = Shader.PropertyToID("_Radius");

    [Header("Inputs")]
    [SerializeField] private FrameProvider sourceMask;   // R32_SInt 0/1

    [Header("Params")]
    [SerializeField, Range(0, 32)] private int radius = 3; // Chebyshev radius

    [Header("Shader/Output")]
    [SerializeField] private ComputeShader shader;   // Assets/Shaders/Edge/EdgeDilate.compute
    [SerializeField] private RenderTexture output;   // R32_SInt 0/1

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
        if (radius < 0) radius = 0;
    }

    private void Subscribe()
    {
        if (sourceMask != null) { sourceMask.OnFrameTexInit += OnAnyInit; sourceMask.OnFrameUpdated += OnAnyUpdated; }
    }

    private void Unsubscribe()
    {
        if (sourceMask != null) { sourceMask.OnFrameTexInit -= OnAnyInit; sourceMask.OnFrameUpdated -= OnAnyUpdated; }
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
        if (sourceMask == null) throw new InvalidOperationException("sourceMask is not assigned");
        if (shader == null) throw new InvalidOperationException("ComputeShader is not assigned");
        if (output == null) throw new InvalidOperationException("output RenderTexture must be assigned via Inspector");
    }

    private void TryEnsureOutput()
    {
        EnsureOutputOrThrow();
        if (!IsInitTexture && output.IsCreated()) { OnFrameTexInitialized(); IsInitTexture = true; }
        ready = true;
    }

    private void EnsureOutputOrThrow()
    {
        if (output.graphicsFormat != GraphicsFormat.R32_SInt)
            throw new InvalidOperationException("Output must be R32_SInt (RInt)");
        if (!output.enableRandomWrite)
            throw new InvalidOperationException("Output must have enableRandomWrite=true");
        if (output.width <= 0 || output.height <= 0)
            throw new InvalidOperationException("Output size must be positive");
        if (!output.IsCreated()) output.Create();
    }

    private void TryDispatch()
    {
        if (!ready) { TryEnsureOutput(); if (!ready) return; }

        var sTex = sourceMask.FrameTex;
        if (sTex == null || output == null) return;
        if (!sTex.IsCreated() || !output.IsCreated()) return;
        if (sTex.graphicsFormat != GraphicsFormat.R32_SInt)
            throw new InvalidOperationException("sourceMask must be R32_SInt (0/1)");
        if (sTex.width != output.width || sTex.height != output.height)
            throw new InvalidOperationException("sourceMask size must match output size");

        shader.SetTexture(kernel, PropInput, sTex);
        shader.SetTexture(kernel, PropOutput, output);
        shader.SetInts(PropOutSize, output.width, output.height);
        shader.SetInt(PropRadius, radius);

        int gx = Mathf.CeilToInt(output.width / (float)tgx);
        int gy = Mathf.CeilToInt(output.height / (float)tgy);
        shader.Dispatch(kernel, gx, gy, 1);

        lastTs = sourceMask.TimeStamp;
        TickUp();
    }
}


