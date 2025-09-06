using System;
using UnityEngine;

[DisallowMultipleComponent]
public class CannyEdgeProvider : FrameProvider
{
    [Header("Input Source")]
    [SerializeField] private FrameProvider sourceProvider;

    [Header("Compute")]
    [SerializeField] private ComputeShader cannyCompute;
    [SerializeField] private Vector3 lumaWeights = new Vector3(0.299f, 0.587f, 0.114f);
    [SerializeField, Range(0.0f, 1.0f)] private float lowThreshold = 0.04f;
    [SerializeField, Range(0.0f, 1.0f)] private float highThreshold = 0.10f;
    [SerializeField, Range(1, 4)] private int hysteresisPasses = 2;

    [Header("Output (RFloat 0..1)")]
    [SerializeField] private RenderTexture edgeTex;

    // Intermediates
    private RenderTexture _lumaRT;
    private RenderTexture _magRT;
    private RenderTexture _dirRT; // RGFloat
    private RenderTexture _nmsRT;
    private RenderTexture _edgePing;
    private RenderTexture _edgePong;

    private DateTime _timestamp;
    private int _kGauss;
    private int _kSobel;
    private int _kNms;
    private int _kHyst;
    private bool _ownsOutput;

    public override RenderTexture FrameTex => edgeTex;
    public override DateTime TimeStamp => _timestamp;

    private void OnEnable()
    {
        if (sourceProvider == null) throw new NullReferenceException("CannyEdgeProvider: sourceProvider not assigned");
        if (cannyCompute == null) throw new NullReferenceException("CannyEdgeProvider: cannyCompute not assigned");
        _kGauss = cannyCompute.FindKernel("KGaussLuma");
        _kSobel = cannyCompute.FindKernel("KSobel");
        _kNms   = cannyCompute.FindKernel("KNms");
        _kHyst  = cannyCompute.FindKernel("KHysteresis");

        sourceProvider.OnFrameTexInit += OnSourceInit;
        sourceProvider.OnFrameUpdated += OnSourceUpdated;
        if (sourceProvider.IsInitTexture)
        {
            OnSourceInit(sourceProvider.FrameTex);
        }
    }

    private void OnDisable()
    {
        if (sourceProvider != null)
        {
            sourceProvider.OnFrameTexInit -= OnSourceInit;
            sourceProvider.OnFrameUpdated -= OnSourceUpdated;
        }
        if (_ownsOutput) ReleaseRT(ref edgeTex); else edgeTex = null;
        ReleaseRT(ref _lumaRT);
        ReleaseRT(ref _magRT);
        ReleaseRT(ref _dirRT);
        ReleaseRT(ref _nmsRT);
        ReleaseRT(ref _edgePing);
        ReleaseRT(ref _edgePong);
        IsInitTexture = false;
    }

    private void OnSourceInit(RenderTexture src)
    {
        _ownsOutput = edgeTex == null;
        EnsureAllRT(src.width, src.height);
        ClearRT(edgeTex, Color.black);
        OnFrameTexInitialized();
        IsInitTexture = true;
    }

    private void OnSourceUpdated(RenderTexture src)
    {
        if (!IsInitTexture || src == null) return;
        EnsureAllRT(src.width, src.height);
        DispatchCanny(src);
        _timestamp = DateTime.Now;
        TickUp();
    }

    private void EnsureAllRT(int w, int h)
    {
        EnsureEdgeOutput(w, h);
        EnsureRT(ref _lumaRT, w, h, RenderTextureFormat.RFloat, true);
        EnsureRT(ref _magRT,  w, h, RenderTextureFormat.RFloat, true);
        EnsureRT(ref _dirRT,  w, h, RenderTextureFormat.RGFloat, true);
        EnsureRT(ref _nmsRT,  w, h, RenderTextureFormat.RFloat, true);
        EnsureRT(ref _edgePing, w, h, RenderTextureFormat.RFloat, true);
        EnsureRT(ref _edgePong, w, h, RenderTextureFormat.RFloat, true);
    }

    private void EnsureEdgeOutput(int w, int h)
    {
        if (edgeTex != null)
        {
            if (edgeTex.width == w && edgeTex.height == h) return;
            if (!_ownsOutput)
            {
                Debug.LogWarning("CannyEdgeProvider: Provided output RT size mismatch; skipping update to avoid modifying assets.");
                return;
            }
            ReleaseRT(ref edgeTex);
        }
        if (_ownsOutput)
        {
            edgeTex = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat)
            {
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            edgeTex.Create();
        }
    }

    private static void EnsureRT(ref RenderTexture rt, int w, int h, RenderTextureFormat fmt, bool uav)
    {
        if (rt != null && rt.width == w && rt.height == h && rt.format == fmt) return;
        ReleaseRT(ref rt);
        rt = new RenderTexture(w, h, 0, fmt)
        {
            enableRandomWrite = uav,
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        rt.Create();
    }

    private void DispatchCanny(RenderTexture src)
    {
        int gx = Mathf.CeilToInt(src.width / 16.0f);
        int gy = Mathf.CeilToInt(src.height / 16.0f);

        // 1) Gaussian -> _lumaRT
        cannyCompute.SetInt("_Width", src.width);
        cannyCompute.SetInt("_Height", src.height);
        cannyCompute.SetVector("_LumaWeights", lumaWeights);
        cannyCompute.SetTexture(_kGauss, "_SourceTex", src);
        cannyCompute.SetTexture(_kGauss, "_LumaTex", _lumaRT);
        cannyCompute.Dispatch(_kGauss, gx, gy, 1);

        // 2) Sobel -> _magRT, _dirRT
        cannyCompute.SetTexture(_kSobel, "_LumaIn", _lumaRT);
        cannyCompute.SetTexture(_kSobel, "_MagTex", _magRT);
        cannyCompute.SetTexture(_kSobel, "_DirTex", _dirRT);
        cannyCompute.Dispatch(_kSobel, gx, gy, 1);

        // 3) NMS -> _nmsRT
        cannyCompute.SetTexture(_kNms, "_MagIn", _magRT);
        cannyCompute.SetTexture(_kNms, "_DirIn", _dirRT);
        cannyCompute.SetTexture(_kNms, "_NmsTex", _nmsRT);
        cannyCompute.Dispatch(_kNms, gx, gy, 1);

        // 4) Hysteresis: ping-pong passes (final write prefers direct compute write into edgeTex)
        cannyCompute.SetFloat("_LowThreshold", Mathf.Min(lowThreshold, highThreshold));
        cannyCompute.SetFloat("_HighThreshold", Mathf.Max(lowThreshold, highThreshold));
        RenderTexture readEdge = _edgePing;
        RenderTexture writeEdge = _edgePong;

        // Seed: clear readEdge to zeros
        ClearRT(readEdge, Color.black);

        int passes = Mathf.Max(1, hysteresisPasses);

        // Run first (passes-1) ping-pong iterations, keeping result in readEdge
        for (int pass = 0; pass < passes - 1; ++pass)
        {
            cannyCompute.SetTexture(_kHyst, "_NmsIn", _nmsRT);
            cannyCompute.SetTexture(_kHyst, "_EdgeIn", readEdge);
            cannyCompute.SetTexture(_kHyst, "_EdgeOut", writeEdge);
            cannyCompute.Dispatch(_kHyst, gx, gy, 1);

            // swap
            var tmp = readEdge; readEdge = writeEdge; writeEdge = tmp;
        }

        // Final pass: prefer compute write directly into edgeTex (requires UAV)
        bool canWriteDirect = (edgeTex != null && edgeTex.enableRandomWrite);
        if (canWriteDirect)
        {
            cannyCompute.SetTexture(_kHyst, "_NmsIn", _nmsRT);
            cannyCompute.SetTexture(_kHyst, "_EdgeIn", readEdge);
            cannyCompute.SetTexture(_kHyst, "_EdgeOut", edgeTex);
            cannyCompute.Dispatch(_kHyst, gx, gy, 1);
        }
        else
        {
            // Fallback: finish into a temp and blit to edgeTex to keep compatibility when edgeTex isn't UAV-capable
            cannyCompute.SetTexture(_kHyst, "_NmsIn", _nmsRT);
            cannyCompute.SetTexture(_kHyst, "_EdgeIn", readEdge);
            cannyCompute.SetTexture(_kHyst, "_EdgeOut", writeEdge);
            cannyCompute.Dispatch(_kHyst, gx, gy, 1);
            // swap to make writeEdge the final read
            readEdge = writeEdge;
            Graphics.Blit(readEdge, edgeTex);
        }
    }

    private static void ClearRT(RenderTexture rt, Color c)
    {
        var active = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, c);
        RenderTexture.active = active;
    }

    private static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt == null) return;
        if (rt.IsCreated()) rt.Release();
        rt = null;
    }
}


