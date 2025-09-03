using System;
using UnityEngine;

[DisallowMultipleComponent]
public class SobelEdgeProvider : FrameProvider
{
    [Header("Input Source")]
    [SerializeField] private FrameProvider sourceProvider;

    [Header("Compute")]
    [SerializeField] private ComputeShader sobelCompute;
    [SerializeField] private Vector3 lumaWeights = new Vector3(0.299f, 0.587f, 0.114f);
    [SerializeField, Range(0.1f, 8f)] private float gain = 1.0f;

    [Header("Output (RFloat 0..1)")]
    [SerializeField] private RenderTexture edgeTex;

    private DateTime _timestamp;
    private int _kernel;
    private bool _ownsOutput;
    private RenderTexture _edgeInternal;

    public override RenderTexture FrameTex => edgeTex;
    public override DateTime TimeStamp => _timestamp;

    private void OnEnable()
    {
        if (sourceProvider == null) throw new NullReferenceException("SobelEdgeProvider: sourceProvider not assigned");
        if (sobelCompute == null) throw new NullReferenceException("SobelEdgeProvider: sobelCompute not assigned");
        _kernel = sobelCompute.FindKernel("CSMain");
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
        if (_ownsOutput) ReleaseRT(ref edgeTex);
        ReleaseRT(ref _edgeInternal);
        IsInitTexture = false;
    }

    private void OnSourceInit(RenderTexture src)
    {
        _ownsOutput = edgeTex == null;
        EnsureEdgeRT(src.width, src.height);
        ClearEdge();
        OnFrameTexInitialized();
        IsInitTexture = true;
    }

    private void OnSourceUpdated(RenderTexture src)
    {
        if (!IsInitTexture || src == null) return;
        EnsureEdgeRT(src.width, src.height);
        var uavTarget = _ownsOutput ? edgeTex : _edgeInternal;
        DispatchSobel(src, uavTarget);
        if (!_ownsOutput && edgeTex != null)
        {
            Graphics.Blit(_edgeInternal, edgeTex);
        }
        _timestamp = DateTime.Now;
        TickUp();
    }

    private void EnsureEdgeRT(int w, int h)
    {
        if (_ownsOutput)
        {
            if (edgeTex != null && edgeTex.width == w && edgeTex.height == h) return;
            ReleaseRT(ref edgeTex);
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
        else
        {
            if (_edgeInternal != null && _edgeInternal.width == w && _edgeInternal.height == h) return;
            ReleaseRT(ref _edgeInternal);
            _edgeInternal = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat)
            {
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _edgeInternal.Create();
        }
    }

    private void DispatchSobel(RenderTexture src, RenderTexture dst)
    {
        sobelCompute.SetInt("_Width", src.width);
        sobelCompute.SetInt("_Height", src.height);
        sobelCompute.SetFloat("_Gain", gain);
        sobelCompute.SetVector("_LumaWeights", lumaWeights);
        sobelCompute.SetTexture(_kernel, "_SourceTex", src);
        sobelCompute.SetTexture(_kernel, "_EdgeTex", dst);

        int gx = Mathf.CeilToInt(src.width / 16.0f);
        int gy = Mathf.CeilToInt(src.height / 16.0f);
        sobelCompute.Dispatch(_kernel, gx, gy, 1);
    }

    private void ClearEdge()
    {
        var active = RenderTexture.active;
        if (_ownsOutput)
        {
            RenderTexture.active = edgeTex;
            GL.Clear(false, true, Color.black);
        }
        else if (_edgeInternal != null)
        {
            RenderTexture.active = _edgeInternal;
            GL.Clear(false, true, Color.black);
        }
        RenderTexture.active = active;
    }

    private static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt == null) return;
        if (rt.IsCreated()) rt.Release();
        rt = null;
    }
}


