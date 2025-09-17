using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[DisallowMultipleComponent]
public sealed class EdgeMaskProvider : FrameProvider
{
    private const string KERNEL_NAME = "CSMain";

    private static readonly int PropEdge = Shader.PropertyToID("_Edge");
    private static readonly int PropOutput = Shader.PropertyToID("output");
    private static readonly int PropOutSize = Shader.PropertyToID("_OutSize");
    private static readonly int PropEdgeSize = Shader.PropertyToID("_EdgeSize");
    private static readonly int PropEdgeTh = Shader.PropertyToID("_EdgeTh");

    [Header("Inputs")]
    [SerializeField] private FrameProvider edge;           // RFloat 0..1

    [Header("Params")]
    [SerializeField, Range(0f,1f)] private float edgeTh = 0.5f;

    [Header("Shader/Output")]
    [SerializeField] private ComputeShader shader;   // Assets/Shaders/Edge/EdgeMask.compute
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
        edgeTh = Mathf.Clamp01(edgeTh);
    }

    private void Subscribe()
    {
        if (edge != null) { edge.OnFrameTexInit += OnAnyInit; edge.OnFrameUpdated += OnAnyUpdated; }
    }

    private void Unsubscribe()
    {
        if (edge != null) { edge.OnFrameTexInit -= OnAnyInit; edge.OnFrameUpdated -= OnAnyUpdated; }
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
        if (edge == null) throw new InvalidOperationException("edge provider is not assigned");
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

        var eTex = edge.FrameTex;
        if (output == null || eTex == null) return;
        if (!output.IsCreated() || !eTex.IsCreated()) return;

        eTex.wrapMode = TextureWrapMode.Clamp;
        eTex.filterMode = FilterMode.Bilinear;

        shader.SetTexture(kernel, PropEdge, eTex);
        shader.SetTexture(kernel, PropOutput, output);
        shader.SetInts(PropOutSize, output.width, output.height);
        shader.SetInts(PropEdgeSize, eTex.width, eTex.height);
        shader.SetFloat(PropEdgeTh, edgeTh);

        int gx = Mathf.CeilToInt(output.width / (float)tgx);
        int gy = Mathf.CeilToInt(output.height / (float)tgy);
        shader.Dispatch(kernel, gx, gy, 1);

        lastTs = edge.TimeStamp;
        TickUp();
    }
}


