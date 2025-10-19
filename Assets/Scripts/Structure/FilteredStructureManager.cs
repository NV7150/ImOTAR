using System;
using UnityEngine;

[DisallowMultipleComponent]
public class FilteredStructureManager : StructureManager {
    [Header("Inputs")]
    [SerializeField] private AsyncFrameProvider depthSource;     // depth frames provider (RFloat meters)
    [SerializeField] private IntrinsicProvider intrinsicProvider; // for intrinsics

    [Header("Compute")]
    [SerializeField] private ComputeShader filterCreator;         // FilterStructureCreator.compute (CSMain)

    [Header("Radius Params (meters)")]
    [SerializeField] private float rScale = 1.0f;                // r = rScale * z / fx_px
    [SerializeField] private float rMin = 0.0005f;               // clamp lower bound
    [SerializeField] private float rMax = 0.05f;                 // clamp upper bound

    [Header("Filter Params")]
    [SerializeField] private bool enableFilter = true;
    [SerializeField] private int neighborWin = 3;    // 3 or 5 only
    [SerializeField] private int minNeighbor = 2;    // 3x3: max 8, 5x5: max 24
    [SerializeField] private float depthBandAbs = 0.003f; // meters
    [SerializeField] private float depthBandRel = 0.03f;  // ratio

    [Header("Validity")]
    [SerializeField] private float zPosEps = 1e-6f; // z>=eps is valid

    [Header("Debug")] 
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private bool useDebugCompute = false;
    [SerializeField] private string logPrefix = "[FilteredSplatManager]";

    public override Guid Generation => _currentSplat != null ? _currentSplat.JobId : Guid.Empty;

    private bool _hasIntrinsics;
    private float _fxPx, _fyPx, _cxPx, _cyPx;

    private int _kernel;
    private int _propDepthTex, _propPoints, _propW, _propH, _propFx, _propFy, _propCx, _propCy, _propRScale, _propRMin, _propRMax, _propValidCount;
    private int _propEnableFilter, _propNeighborWin, _propMinNeighbor, _propDepthBandAbs, _propDepthBandRel, _propZPosEps;

    private PointCloud _currentSplat;

    private void OnEnable(){
        if (depthSource == null) throw new NullReferenceException("FilteredStructureManager: depthSource not assigned");
        if (intrinsicProvider == null) throw new NullReferenceException("FilteredStructureManager: intrinsicProvider not assigned");
        if (filterCreator == null) throw new NullReferenceException("FilteredStructureManager: filterCreator not assigned");

        _kernel = filterCreator.FindKernel("CSMain");
        _propDepthTex = Shader.PropertyToID("_DepthTex");
        _propPoints   = Shader.PropertyToID("_Points");
        _propW        = Shader.PropertyToID("_Width");
        _propH        = Shader.PropertyToID("_Height");
        _propFx       = Shader.PropertyToID("_FxPx");
        _propFy       = Shader.PropertyToID("_FyPx");
        _propCx       = Shader.PropertyToID("_CxPx");
        _propCy       = Shader.PropertyToID("_CyPx");
        _propRScale   = Shader.PropertyToID("_RScale");
        _propRMin     = Shader.PropertyToID("_RMin");
        _propRMax     = Shader.PropertyToID("_RMax");
        _propValidCount = Shader.PropertyToID("_ValidCount");

        _propEnableFilter = Shader.PropertyToID("_EnableFilter");
        _propNeighborWin  = Shader.PropertyToID("_NeighborWin");
        _propMinNeighbor  = Shader.PropertyToID("_MinNeighbor");
        _propDepthBandAbs = Shader.PropertyToID("_DepthBandAbs");
        _propDepthBandRel = Shader.PropertyToID("_DepthBandRel");
        _propZPosEps      = Shader.PropertyToID("_ZPosEps");

        depthSource.OnAsyncFrameUpdated += OnDepthJobCompleted;
        depthSource.OnAsyncFrameCanceled += OnDepthJobCanceled;

        StartCoroutine(WaitForIntrinsics());
    }

    private void OnDisable(){
        if (depthSource != null){
            depthSource.OnAsyncFrameUpdated -= OnDepthJobCompleted;
            depthSource.OnAsyncFrameCanceled -= OnDepthJobCanceled;
        }
        if (_currentSplat != null){
            _currentSplat.Dispose();
            _currentSplat = null;
        }
    }

    private System.Collections.IEnumerator WaitForIntrinsics(){
        while (!_hasIntrinsics){
            TryInitIntrinsics();
            if (_hasIntrinsics) break;
            yield return null;
        }
        if (verboseLogging) Debug.Log($"{logPrefix} Intrinsics ready: fx={_fxPx:F2} fy={_fyPx:F2} cx={_cxPx:F2} cy={_cyPx:F2}");
    }

    private void TryInitIntrinsics(){
        if (_hasIntrinsics) return;
        if (intrinsicProvider != null){
            var intrinsics = intrinsicProvider.GetIntrinsics();
            if (intrinsics.isValid){
                _fxPx = intrinsics.fxPx;
                _fyPx = intrinsics.fyPx;
                _cxPx = intrinsics.cxPx;
                _cyPx = intrinsics.cyPx;
                _hasIntrinsics = true;
            }
        }
    }

    private void ValidateParams(int w, int h){
        if (w <= 0 || h <= 0) throw new InvalidOperationException("FilteredStructureManager: invalid depth size");
        if (neighborWin != 3 && neighborWin != 5) throw new ArgumentOutOfRangeException(nameof(neighborWin), "neighborWin must be 3 or 5");
        int maxN = (neighborWin == 3) ? 8 : 24; // center excluded
        if (minNeighbor < 0 || minNeighbor > maxN) throw new ArgumentOutOfRangeException(nameof(minNeighbor), $"minNeighbor must be within [0,{maxN}]");
    }

    private void OnDepthJobCompleted(AsyncFrame frame){

        if (!_hasIntrinsics) throw new InvalidOperationException("FilteredStructureManager: intrinsics not ready");
        if (frame.RenderTexture == null) throw new NullReferenceException("FilteredStructureManager: frame RenderTexture is null");

        var rt = frame.RenderTexture;
        int w = rt.width;
        int h = rt.height;

        ValidateParams(w, h);

        // Scale intrinsics to depth texture resolution
        var intrinsics = intrinsicProvider.GetIntrinsics();
        var scaledIntrinsics = IntrinsicScaler.ScaleToOutput(intrinsics, w, h);

        // allocate points buffer (float4 per pixel)
        int count = w * h;
        int stride = sizeof(float) * 4;
        var points = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);

        // allocate valid count buffer (always needed for debug shader)
        var validCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
        validCount.SetData(new uint[]{0});

        // bind and dispatch
        filterCreator.SetTexture(_kernel, _propDepthTex, rt);
        filterCreator.SetBuffer(_kernel, _propPoints, points);
        filterCreator.SetBuffer(_kernel, _propValidCount, validCount);
        filterCreator.SetInt(_propW, w);
        filterCreator.SetInt(_propH, h);
        filterCreator.SetFloat(_propFx, scaledIntrinsics.fxPx);
        filterCreator.SetFloat(_propFy, scaledIntrinsics.fyPx);
        filterCreator.SetFloat(_propCx, scaledIntrinsics.cxPx);
        filterCreator.SetFloat(_propCy, scaledIntrinsics.cyPx);
        filterCreator.SetFloat(_propRScale, rScale);
        filterCreator.SetFloat(_propRMin, rMin);
        filterCreator.SetFloat(_propRMax, rMax);
        filterCreator.SetFloat(_propZPosEps, zPosEps);

        // filter params
        filterCreator.SetInt(_propEnableFilter, enableFilter ? 1 : 0);
        filterCreator.SetInt(_propNeighborWin, neighborWin);
        filterCreator.SetInt(_propMinNeighbor, minNeighbor);
        filterCreator.SetFloat(_propDepthBandAbs, depthBandAbs);
        filterCreator.SetFloat(_propDepthBandRel, depthBandRel);

        int gx = (w + 7) / 8;
        int gy = (h + 7) / 8;
        filterCreator.Dispatch(_kernel, gx, gy, 1);

        // read valid count (for debug) and dispose
        uint[] countData = new uint[1];
        validCount.GetData(countData);
        if (verboseLogging) Debug.Log($"{logPrefix} Valid points: {countData[0]} / {count}");
        validCount.Dispose();

        // replace current splat
        if (_currentSplat != null){
            _currentSplat.Dispose();
            _currentSplat = null;
        }
        // Use full buffer length; shader will mask out holes at draw
        _currentSplat = new PointCloud(points, count, frame.Id);
        if (verboseLogging)
            Debug.Log($"{logPrefix} Splat ready id={frame.Id} count={count}");
        base.InvokeReady(_currentSplat);
    }

    private void OnDepthJobCanceled(Guid jobId){
        if (verboseLogging) Debug.Log($"{logPrefix} JobCanceled id={jobId}");
    }
}


