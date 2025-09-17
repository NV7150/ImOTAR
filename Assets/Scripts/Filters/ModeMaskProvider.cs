using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// Applies spatial mode filter over an integer mask texture (R32_SInt).
/// - No intermediate RenderTexture; writes directly to output.
/// - Event-driven: subscribes to source FrameProvider updates.
/// - Window size is odd (>=3) and clamped by shader MAX.
/// - Tie break policy is selectable.
/// </summary>
public sealed class ModeMaskProvider : FrameProvider
{
    private const string KERNEL_NAME = "CSMain";
    private const int MAX_KERNEL = 15; // Must mirror shader MAX_KERNEL

    private static readonly int PropInput = Shader.PropertyToID("_Input");
    private static readonly int PropOutput = Shader.PropertyToID("output");
    private static readonly int PropSize = Shader.PropertyToID("_Size");
    private static readonly int PropKernelSize = Shader.PropertyToID("_KernelSize");
    private static readonly int PropTieRule = Shader.PropertyToID("_TieRule");

    public enum TieRule
    {
        Center = 0,
        Smallest = 1,
        Largest = 2
    }

    [Header("Source (event-driven)")]
    [SerializeField] private FrameProvider source;

    [Header("Params")]
    [Tooltip("Odd window size (>=3). Must be <= shader MAX.")]
    [SerializeField] private int windowSize = 3;
    [SerializeField] private TieRule tieRule = TieRule.Center;

    [Header("Shader/Output")]
    [SerializeField] private ComputeShader shader;   // Assets/Shaders/MaskMode.compute
    [SerializeField] private RenderTexture output;   // R32_SInt, external asset; Create() here

    private int kernel;
    private bool isReady;
    private DateTime lastTimestamp;
    private uint threadX = 1, threadY = 1, threadZ = 1;

    public override RenderTexture FrameTex => output;
    public override DateTime TimeStamp => lastTimestamp;

    private void OnEnable()
    {
        ValidateSerialized();
        kernel = shader.FindKernel(KERNEL_NAME);
        if (kernel < 0) throw new InvalidOperationException($"Kernel '{KERNEL_NAME}' not found in shader {shader.name}");
        shader.GetKernelThreadGroupSizes(kernel, out threadX, out threadY, out threadZ);

        source.OnFrameTexInit += OnAnyInit;
        source.OnFrameUpdated += OnAnyUpdated;

        TryEnsureOutput();
    }

    private void OnDisable()
    {
        if (source != null)
        {
            source.OnFrameTexInit -= OnAnyInit;
            source.OnFrameUpdated -= OnAnyUpdated;
        }
    }

    private void OnValidate()
    {
        if (windowSize < 3) windowSize = 3;
        if ((windowSize & 1) == 0) windowSize += 1; // force odd
    }

    private void ValidateSerialized()
    {
        if (source == null) throw new InvalidOperationException("source is not assigned");
        if (shader == null) throw new InvalidOperationException("ComputeShader is not assigned");
        if (output == null) throw new InvalidOperationException("output RenderTexture must be assigned via Inspector");
        if (windowSize < 3 || (windowSize & 1) == 0)
            throw new InvalidOperationException("windowSize must be odd and >= 3");
        if (windowSize > MAX_KERNEL)
            throw new InvalidOperationException($"windowSize must be <= {MAX_KERNEL}");
        if (!Enum.IsDefined(typeof(TieRule), tieRule))
            throw new InvalidOperationException("Invalid tieRule");
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
        var sTex = source.FrameTex;
        if (sTex == null) return;
        if (!sTex.IsCreated()) return;

        EnsureInputFormatOrThrow(sTex);
        EnsureOutputOrThrow(sTex);

        if (!IsInitTexture && output.IsCreated())
        {
            OnFrameTexInitialized();
            IsInitTexture = true;
        }

        isReady = true;
    }

    private static void EnsureInputFormatOrThrow(RenderTexture sTex)
    {
        if (sTex.graphicsFormat != GraphicsFormat.R32_SInt)
            throw new InvalidOperationException("Input RenderTexture must be R32_SInt (RInt)");
    }

    private void EnsureOutputOrThrow(RenderTexture sTex)
    {
        if (output.graphicsFormat != GraphicsFormat.R32_SInt)
            throw new InvalidOperationException("Output RenderTexture must be R32_SInt (RInt)");
        if (!output.enableRandomWrite)
            throw new InvalidOperationException("Output RenderTexture must have enableRandomWrite=true");
        if (output.width != sTex.width || output.height != sTex.height)
            throw new InvalidOperationException("Output size must match source size");
        if (!output.IsCreated()) output.Create();
    }

    private void TryDispatch()
    {
        if (!isReady) { TryEnsureOutput(); if (!isReady) return; }

        var sTex = source.FrameTex;
        if (sTex == null) return;
        if (!sTex.IsCreated()) return;

        // Set parameters
        shader.SetTexture(kernel, PropInput, sTex);
        shader.SetTexture(kernel, PropOutput, output);
        shader.SetInts(PropSize, sTex.width, sTex.height);
        shader.SetInt(PropKernelSize, windowSize);
        shader.SetInt(PropTieRule, (int)tieRule);

        int tgx = Mathf.CeilToInt(sTex.width / (float)threadX);
        int tgy = Mathf.CeilToInt(sTex.height / (float)threadY);
        shader.Dispatch(kernel, tgx, tgy, 1);

        lastTimestamp = source.TimeStamp;
        TickUp();
    }
}


