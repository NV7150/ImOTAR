using System;
using UnityEngine;

/// <summary>
/// Poisson-type depth densification using edge-guided smoothness and temporal consistency.
/// - Input sparse depth: RFloat meters (invalid < 0)
/// - Input edge: RFloat 0..1 (continuous). Sampled to depth grid (nearest/bilinear).
/// - Output: RFloat meters, written directly by in-place red-black Gauss–Seidel.
/// - History: RFloat meters, same size as output; used as temporal prior and updated each frame.
/// No CPU-side intermediate RenderTextures are created.
/// </summary>
[DisallowMultipleComponent]
public sealed class PoissonRefiner : FrameProvider
{
    [Header("Inputs")]
    [SerializeField] private FrameProvider sparseProvider; // RFloat meters, invalid < 0
    [SerializeField] private FrameProvider edgeProvider;   // RFloat 0..1

    [Header("Compute")]
    [SerializeField] private ComputeShader poissonCompute;

    [Header("Params")]
    [SerializeField] private int iterations = 32;
    [SerializeField] private Vector3 lambda = new Vector3(1f, 0.01f, 1f); // (lambda_d, lambda_t, lambda_s)
    [SerializeField] private float epsDen = 1e-6f;
    [SerializeField] private float edgeScale = 1.0f; // smoothness suppression by edge strength
    [SerializeField] private bool edgeBilinear = true; // sample mode for edge input
    [SerializeField] private bool hardEdgeCut = false; // not used by default

    [Header("Output")]
    [SerializeField] private RenderTexture output;  // RFloat, UAV
    [SerializeField] private RenderTexture history; // RFloat, UAV

    [Header("Debug")]
    [SerializeField] private bool verboseLogs = false;

    public override RenderTexture FrameTex => output;
    public override DateTime TimeStamp => timeStamp;

    private int kInit;
    private int kRed;
    private int kBlack;
    private int kFinal;

    private DateTime timeStamp;
    private bool ready;
    private bool ownsOutput;
    private bool ownsHistory;
    private int lastSparseTick = -1;
    private int lastEdgeTick = -1;

    private void OnEnable()
    {
        if (poissonCompute == null) throw new NullReferenceException("PoissonRefiner: compute shader not assigned");
        if (sparseProvider == null) throw new NullReferenceException("PoissonRefiner: sparseProvider not assigned");
        if (edgeProvider == null) throw new NullReferenceException("PoissonRefiner: edgeProvider not assigned");

        kInit = poissonCompute.FindKernel("KInit");
        kRed = poissonCompute.FindKernel("KGSRed");
        kBlack = poissonCompute.FindKernel("KGSBlack");
        kFinal = poissonCompute.FindKernel("KFinalize");

        sparseProvider.OnFrameTexInit += OnInputInit;
        sparseProvider.OnFrameUpdated += OnInputUpdated;
        edgeProvider.OnFrameTexInit += OnInputInit;
        edgeProvider.OnFrameUpdated += OnInputUpdated;

        TryEnsureTargets();
        TryRun();
    }

    private void OnDisable()
    {
        if (sparseProvider != null)
        {
            sparseProvider.OnFrameTexInit -= OnInputInit;
            sparseProvider.OnFrameUpdated -= OnInputUpdated;
        }
        if (edgeProvider != null)
        {
            edgeProvider.OnFrameTexInit -= OnInputInit;
            edgeProvider.OnFrameUpdated -= OnInputUpdated;
        }
        if (ownsOutput)
        {
            SafeRelease(ref output);
        }
        if (ownsHistory)
        {
            SafeRelease(ref history);
        }
        ready = false;
        IsInitTexture = false;
    }

    private void OnInputInit(RenderTexture _)
    {
        TryEnsureTargets();
    }

    private void OnInputUpdated(RenderTexture _)
    {
        TryRun();
    }

    private void TryEnsureTargets()
    {
        var s = sparseProvider.FrameTex;
        var e = edgeProvider.FrameTex;
        if (s == null || e == null) return;
        if (!s.IsCreated() || !e.IsCreated()) return;

        EnsureInputFormatOrThrow(s, "sparse depth");
        EnsureInputFormatOrThrow(e, "edge");
        EnsureOutputOrThrow(s);
        EnsureHistoryOrThrow(s);

        ready = true;
        if (!IsInitTexture && output != null && output.IsCreated())
        {
            IsInitTexture = true;
            OnFrameTexInitialized();
        }
    }

    private static void EnsureInputFormatOrThrow(RenderTexture tex, string label)
    {
        if (tex.format != RenderTextureFormat.RFloat)
            throw new InvalidOperationException($"PoissonRefiner: {label} must be RenderTextureFormat.RFloat");
    }

    private void EnsureOutputOrThrow(RenderTexture like)
    {
        if (output == null)
        {
            output = NewRFloat(like.width, like.height, true, FilterMode.Point);
            ownsOutput = true;
        }
        else
        {
            if (output.format != RenderTextureFormat.RFloat || !output.enableRandomWrite)
                throw new InvalidOperationException("PoissonRefiner: output must be RFloat and enableRandomWrite=true");
            if (output.width != like.width || output.height != like.height)
                throw new InvalidOperationException("PoissonRefiner: output size must match sparse depth size");
            if (!output.IsCreated()) output.Create();
        }
    }

    private void EnsureHistoryOrThrow(RenderTexture like)
    {
        if (history == null)
        {
            history = NewRFloat(like.width, like.height, true, FilterMode.Point);
            ownsHistory = true;
            // Initialize history to invalid (<0) to avoid false temporal seeds.
            ClearRT(history, -1.0f);
        }
        else
        {
            if (history.format != RenderTextureFormat.RFloat || !history.enableRandomWrite)
                throw new InvalidOperationException("PoissonRefiner: history must be RFloat and enableRandomWrite=true");
            if (history.width != like.width || history.height != like.height)
                throw new InvalidOperationException("PoissonRefiner: history size must match sparse depth size");
            if (!history.IsCreated())
            {
                history.Create();
                ClearRT(history, -1.0f);
            }
        }
    }

    private static RenderTexture NewRFloat(int w, int h, bool uav, FilterMode filter)
    {
        var rt = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat)
        {
            enableRandomWrite = uav,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = filter
        };
        rt.Create();
        return rt;
    }

    private static void SafeRelease(ref RenderTexture rt)
    {
        if (rt != null)
        {
            if (rt.IsCreated()) rt.Release();
            rt = null;
        }
    }

    private void TryRun()
    {
        if (!ready)
        {
            TryEnsureTargets();
            if (!ready) return;
        }

        var s = sparseProvider.FrameTex;
        var e = edgeProvider.FrameTex;
        if (s == null || e == null) return;
        if (!s.IsCreated() || !e.IsCreated()) return;

        // Skip if both ticks didn't change
        int st = sparseProvider.Tick;
        int et = edgeProvider.Tick;
        if (st == lastSparseTick && et == lastEdgeTick) return;

        DispatchAll(s, e);

        lastSparseTick = st;
        lastEdgeTick = et;
        timeStamp = sparseProvider.TimeStamp;
        TickUp();
    }

    private void DispatchAll(RenderTexture sparse, RenderTexture edge)
    {
        EnsureInputFormatOrThrow(sparse, "sparse depth");
        EnsureInputFormatOrThrow(edge, "edge");
        EnsureOutputOrThrow(sparse);
        EnsureHistoryOrThrow(sparse);

        int w = sparse.width;
        int h = sparse.height;
        int ew = edge.width;
        int eh = edge.height;

        poissonCompute.SetInt("_Width", w);
        poissonCompute.SetInt("_Height", h);
        poissonCompute.SetInt("_EdgeWidth", ew);
        poissonCompute.SetInt("_EdgeHeight", eh);
        poissonCompute.SetVector("_Lambda", lambda);
        poissonCompute.SetFloat("_EpsDen", epsDen);
        poissonCompute.SetFloat("_EdgeScale", edgeScale);
        poissonCompute.SetInt("_EdgeBilinear", edgeBilinear ? 1 : 0);
        poissonCompute.SetInt("_HardEdgeCut", hardEdgeCut ? 1 : 0);
        poissonCompute.SetFloat("_ValidThresh", 0.0f);

        int gx = Mathf.CeilToInt(w / 16.0f);
        int gy = Mathf.CeilToInt(h / 16.0f);

        // KInit: write initial solution into output
        poissonCompute.SetTexture(kInit, "_SparseDepth", sparse);
        poissonCompute.SetTexture(kInit, "_TempDepth", history);
        poissonCompute.SetTexture(kInit, "_Output", output);
        poissonCompute.Dispatch(kInit, gx, gy, 1);

        // Red-Black GS iterations, in-place on _Output
        poissonCompute.SetTexture(kRed, "_SparseDepth", sparse);
        poissonCompute.SetTexture(kRed, "_TempDepth", history);
        poissonCompute.SetTexture(kRed, "_EdgeTex", edge);
        poissonCompute.SetTexture(kRed, "_Output", output);

        poissonCompute.SetTexture(kBlack, "_SparseDepth", sparse);
        poissonCompute.SetTexture(kBlack, "_TempDepth", history);
        poissonCompute.SetTexture(kBlack, "_EdgeTex", edge);
        poissonCompute.SetTexture(kBlack, "_Output", output);

        for (int i = 0; i < iterations; i++)
        {
            poissonCompute.Dispatch(kRed, gx, gy, 1);
            poissonCompute.Dispatch(kBlack, gx, gy, 1);
        }

        // Finalize: copy output to history
        poissonCompute.SetTexture(kFinal, "_Output", output);
        poissonCompute.SetTexture(kFinal, "_History", history);
        poissonCompute.Dispatch(kFinal, gx, gy, 1);

        if (verboseLogs)
        {
            Debug.Log($"[PoissonRefiner] Ran {iterations} GS iters on {w}x{h}, edge {ew}x{eh}");
        }
    }

    private static readonly int ClearKernelHash = "KClear".GetHashCode();
    private int kClear = -1;
    private static readonly int PropClearTex = Shader.PropertyToID("_ClearTex");
    private static readonly int PropClearValue = Shader.PropertyToID("_ClearValue");

    private void EnsureClearKernel()
    {
        if (kClear >= 0) return;
        // Try find optional KClear kernel; if not present, we fallback to CPU clear using GL which writes -1 safely.
        try { kClear = poissonCompute.FindKernel("KClear"); }
        catch { kClear = -1; }
    }

    private void ClearRT(RenderTexture rt, float value)
    {
        EnsureClearKernel();
        if (kClear >= 0)
        {
            int w = rt.width, h = rt.height;
            int gx = Mathf.CeilToInt(w / 16.0f);
            int gy = Mathf.CeilToInt(h / 16.0f);
            poissonCompute.SetTexture(kClear, PropClearTex, rt);
            poissonCompute.SetFloat(PropClearValue, value);
            poissonCompute.SetInt("_Width", w);
            poissonCompute.SetInt("_Height", h);
            poissonCompute.Dispatch(kClear, gx, gy, 1);
        }
        else
        {
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, new Color(value, 0f, 0f, 0f));
            RenderTexture.active = active;
        }
    }
}


