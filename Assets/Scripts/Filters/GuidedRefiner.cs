using System;
using UnityEngine;

/// <summary>
/// Edge-guided depth refinement using a minimal guided filter implemented in a compute shader.
/// - Guidance: Canny edge map (RFloat, 0..1)
/// - Input depth: RFloat meters (same aspect; resolution may differ from edge)
/// - Output: RFloat, same size as input depth
/// - Compute only. No fallbacks. Throws on misconfiguration.
/// </summary>
[DisallowMultipleComponent]
public class GuidedRefiner : DepthRefiner
{
    [Header("Inputs")]
    [Tooltip("Depth source (RFloat, meters). Typically DepthStich.")]
    [SerializeField] private FrameProvider depthProvider;
    [Header("Inputs")]
    [Tooltip("CannyEdgeProvider that supplies an RFloat edge texture (0..1) at RGB resolution.")]
    [SerializeField] private FrameProvider edgeProvider;

    [Header("Compute")]
    [Tooltip("ComputeShader asset for the guided filtering kernels.")]
    [SerializeField] private ComputeShader guidedCompute;

    [Header("Parameters")]
    [Tooltip("Box filter radius in depth pixels (window size = 2*r+1).")]
    [SerializeField, Range(1, 64)] private int radius = 8;

    [Tooltip("Regularization epsilon in meters^2 (stabilizes division).")]
    [SerializeField] private float epsilon = 4e-4f;

    [Tooltip("Edge-based epsilon amplification factor (>=1). Higher values reduce smoothing at strong edges.")]
    [SerializeField] private float edgeEpsScale = 4.0f;

    [Header("Output")]
    [Tooltip("Refined depth output (RFloat, meters). Owned and (re)allocated by this component to match input depth size.")]
    [SerializeField] private RenderTexture output;
    public RenderTexture Output => output;

    // FrameProvider implementation
    public override RenderTexture FrameTex => output;
    public override DateTime TimeStamp => _timestamp;

    // Kernels
    private int _kPrep;
    private int _kBoxH;
    private int _kBoxV;
    private int _kCoeff;
    private int _kCompose;

    // Intermediates (all RFloat, depth-sized, UAV)
    private RenderTexture _gTex;   // guidance at depth grid (edge sampled)
    private RenderTexture _pTex;   // depth copy
    private RenderTexture _g2Tex;
    private RenderTexture _gpTex;

    private RenderTexture _meanG;
    private RenderTexture _meanP;
    private RenderTexture _meanG2;
    private RenderTexture _meanGP;

    private RenderTexture _aTex;
    private RenderTexture _bTex;
    private RenderTexture _meanA;
    private RenderTexture _meanB;

    private RenderTexture _temp;   // box-filter ping for sums

    private bool _kernelsReady;
    private DateTime _timestamp;
    private bool _ownsOutput; // true if we allocated output ourselves

    private void OnEnable()
    {
        if (guidedCompute == null)
            throw new NullReferenceException("GuidedRefiner: ComputeShader not assigned");

        _kPrep    = guidedCompute.FindKernel("KPrep");
        _kBoxH    = guidedCompute.FindKernel("KBoxH");
        _kBoxV    = guidedCompute.FindKernel("KBoxV");
        _kCoeff   = guidedCompute.FindKernel("KCoeff");
        _kCompose = guidedCompute.FindKernel("KCompose");
        _kernelsReady = true;

        // Minimal event-driven wiring inside this component
        if (depthProvider == null) throw new NullReferenceException("GuidedRefiner: depthProvider not assigned");
        if (edgeProvider == null) throw new NullReferenceException("GuidedRefiner: edgeProvider not assigned");

        depthProvider.OnFrameTexInit += OnDepthInit;
        depthProvider.OnFrameUpdated += OnDepthUpdated;
        edgeProvider.OnFrameTexInit += OnEdgeInit;
        edgeProvider.OnFrameUpdated += OnEdgeUpdated;

        // If any already initialized, try a first refine
        if (depthProvider.IsInitTexture && edgeProvider.IsInitTexture)
        {
            TryRefine(depthProvider.FrameTex);
        }
    }

    private void OnDisable()
    {
        if (depthProvider != null)
        {
            depthProvider.OnFrameTexInit -= OnDepthInit;
            depthProvider.OnFrameUpdated -= OnDepthUpdated;
        }
        if (edgeProvider != null)
        {
            edgeProvider.OnFrameTexInit -= OnEdgeInit;
            edgeProvider.OnFrameUpdated -= OnEdgeUpdated;
        }
        if (_ownsOutput)
        {
            ReleaseRT(ref output);
        }
        ReleaseRT(ref _gTex);
        ReleaseRT(ref _pTex);
        ReleaseRT(ref _g2Tex);
        ReleaseRT(ref _gpTex);
        ReleaseRT(ref _meanG);
        ReleaseRT(ref _meanP);
        ReleaseRT(ref _meanG2);
        ReleaseRT(ref _meanGP);
        ReleaseRT(ref _aTex);
        ReleaseRT(ref _bTex);
        ReleaseRT(ref _meanA);
        ReleaseRT(ref _meanB);
        ReleaseRT(ref _temp);
    }

    private void OnDepthInit(RenderTexture depth)
    {
        TryRefine(depth);
    }

    private void OnEdgeInit(RenderTexture edge)
    {
        if (depthProvider != null && depthProvider.IsInitTexture)
        {
            TryRefine(depthProvider.FrameTex);
        }
    }

    private void OnDepthUpdated(RenderTexture depth)
    {
        TryRefine(depth);
    }

    private void OnEdgeUpdated(RenderTexture edge)
    {
        if (depthProvider != null && depthProvider.IsInitTexture)
        {
            TryRefine(depthProvider.FrameTex);
        }
    }

    private void TryRefine(RenderTexture depth)
    {
        if (depth == null) return;
        if (edgeProvider == null || edgeProvider.FrameTex == null) return;
        if (!depth.IsCreated() || !edgeProvider.FrameTex.IsCreated()) return;
        Refine(depth);
        _timestamp = DateTime.Now;
        if (!IsInitTexture && output != null)
        {
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
        TickUp();
    }

    public override RenderTexture Refine(RenderTexture depth)
    {
        if (!_kernelsReady)
            throw new InvalidOperationException("GuidedRefiner: Kernels not initialized (component disabled?)");
        if (depth == null)
            throw new ArgumentNullException(nameof(depth));
    if (edgeProvider == null || edgeProvider.FrameTex == null)
            throw new NullReferenceException("GuidedRefiner: edgeProvider or its FrameTex is null");
        if (guidedCompute == null)
            throw new NullReferenceException("GuidedRefiner: ComputeShader not assigned");
        if (depth.format != RenderTextureFormat.RFloat)
            throw new ArgumentException("GuidedRefiner: depth must be RenderTextureFormat.RFloat");
        if (edgeProvider.FrameTex.format != RenderTextureFormat.RFloat)
            throw new ArgumentException("GuidedRefiner: edge texture must be RenderTextureFormat.RFloat");
        if (radius < 1)
            throw new ArgumentOutOfRangeException(nameof(radius), "radius must be >= 1");
        if (edgeEpsScale < 1.0f)
            throw new ArgumentOutOfRangeException(nameof(edgeEpsScale), "edgeEpsScale must be >= 1");

        int w = depth.width;
        int h = depth.height;

        EnsureOutputAndTemps(w, h);

        // Common params
        int kernelSize = 2 * radius + 1;
        guidedCompute.SetInt("_Width", w);
        guidedCompute.SetInt("_Height", h);
        guidedCompute.SetInt("_Radius", radius);
        guidedCompute.SetInt("_KernelSize", kernelSize);
        guidedCompute.SetFloat("_Epsilon", epsilon);
        guidedCompute.SetFloat("_EdgeEpsScale", edgeEpsScale);

        int gx = Mathf.CeilToInt(w / 16.0f);
        int gy = Mathf.CeilToInt(h / 16.0f);

        // 1) Prepare base fields: G, P, G^2, G*P (at depth grid)
        guidedCompute.SetTexture(_kPrep, "_DepthTex", depth);
        guidedCompute.SetTexture(_kPrep, "_EdgeTex", edgeProvider.FrameTex);
        guidedCompute.SetTexture(_kPrep, "_GTex", _gTex);
        guidedCompute.SetTexture(_kPrep, "_PTex", _pTex);
        guidedCompute.SetTexture(_kPrep, "_G2Tex", _g2Tex);
        guidedCompute.SetTexture(_kPrep, "_GPTex", _gpTex);
        guidedCompute.Dispatch(_kPrep, gx, gy, 1);

        // 2) Box means for G, P, G2, GP (separable H then V)
        BoxFilter(_gTex, _meanG, gx, gy);
        BoxFilter(_pTex, _meanP, gx, gy);
        BoxFilter(_g2Tex, _meanG2, gx, gy);
        BoxFilter(_gpTex, _meanGP, gx, gy);

        // 3) Coefficients a,b
        guidedCompute.SetTexture(_kCoeff, "_MeanG", _meanG);
        guidedCompute.SetTexture(_kCoeff, "_MeanP", _meanP);
        guidedCompute.SetTexture(_kCoeff, "_MeanG2", _meanG2);
        guidedCompute.SetTexture(_kCoeff, "_MeanGP", _meanGP);
        guidedCompute.SetTexture(_kCoeff, "_GTex", _gTex);
        guidedCompute.SetTexture(_kCoeff, "_ATex", _aTex);
        guidedCompute.SetTexture(_kCoeff, "_BTex", _bTex);
        guidedCompute.Dispatch(_kCoeff, gx, gy, 1);

        // 4) Box means for a,b
        BoxFilter(_aTex, _meanA, gx, gy);
        BoxFilter(_bTex, _meanB, gx, gy);

        // 5) Compose q = meanA * G + meanB
        guidedCompute.SetTexture(_kCompose, "_MeanA", _meanA);
        guidedCompute.SetTexture(_kCompose, "_MeanB", _meanB);
        guidedCompute.SetTexture(_kCompose, "_GTex", _gTex);
        guidedCompute.SetTexture(_kCompose, "_Output", output);
        guidedCompute.Dispatch(_kCompose, gx, gy, 1);

        if (verboseLogs)
        {
            Debug.Log($"[GuidedRefiner] Refined {w}x{h} with r={radius}, eps={epsilon}, edgeScale={edgeEpsScale}");
        }

        return output;
    }

    private void BoxFilter(RenderTexture src, RenderTexture dstMean, int gx, int gy)
    {
        // Horizontal sums -> _temp
        guidedCompute.SetTexture(_kBoxH, "_InTex", src);
        guidedCompute.SetTexture(_kBoxH, "_TempTex", _temp);
        guidedCompute.Dispatch(_kBoxH, gx, gy, 1);

        // Vertical sums -> mean (divide by area inside kernel)
        guidedCompute.SetTexture(_kBoxV, "_InTex", _temp);
        guidedCompute.SetTexture(_kBoxV, "_OutTex", dstMean);
        guidedCompute.Dispatch(_kBoxV, gx, gy, 1);
    }

    private void EnsureOutputAndTemps(int w, int h)
    {
        // Output ownership/validation: if user assigned an asset, do not touch it; validate and throw if incompatible.
        if (output == null)
        {
            EnsureRT(ref output, w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
            _ownsOutput = true;
        }
        else
        {
            _ownsOutput = false;
            if (output.width != w || output.height != h || output.format != RenderTextureFormat.RFloat || !output.enableRandomWrite)
            {
                throw new InvalidOperationException(
                    "GuidedRefiner: Provided output RenderTexture must be depth-sized, RFloat, and enableRandomWrite=true. " +
                    "Either clear the Output field to let this component allocate, or assign a compatible RT.");
            }
        }

        EnsureRT(ref _gTex,   w, h, RenderTextureFormat.RFloat, true, FilterMode.Bilinear);
        EnsureRT(ref _pTex,   w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
        EnsureRT(ref _g2Tex,  w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
        EnsureRT(ref _gpTex,  w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);

        EnsureRT(ref _meanG,  w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
        EnsureRT(ref _meanP,  w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
        EnsureRT(ref _meanG2, w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
        EnsureRT(ref _meanGP, w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);

        EnsureRT(ref _aTex,   w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
        EnsureRT(ref _bTex,   w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
        EnsureRT(ref _meanA,  w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
        EnsureRT(ref _meanB,  w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);

        EnsureRT(ref _temp,   w, h, RenderTextureFormat.RFloat, true, FilterMode.Point);
    }

    private static void EnsureRT(ref RenderTexture rt, int w, int h, RenderTextureFormat fmt, bool uav, FilterMode filter)
    {
        if (rt != null && rt.IsCreated() && rt.width == w && rt.height == h && rt.format == fmt && rt.enableRandomWrite == uav)
        {
            // keep existing; ensure desired filter & wrap
            rt.filterMode = filter;
            rt.wrapMode = TextureWrapMode.Clamp;
            return;
        }
        ReleaseRT(ref rt);
        rt = new RenderTexture(w, h, 0, fmt)
        {
            enableRandomWrite = uav,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = filter
        };
        rt.Create();
    }

    private static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt == null) return;
        if (rt.IsCreated()) rt.Release();
        // Do NOT DestroyImmediate here to avoid destroying assets assigned via inspector.
        // Releasing frees GPU memory; we drop the reference to allow GC to collect wrapper.
        rt = null;
    }
}
