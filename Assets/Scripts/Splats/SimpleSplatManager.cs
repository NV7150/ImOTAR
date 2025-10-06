using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SimpleSplatManager : SplatManager {
    [Header("Inputs")]
    [SerializeField] private AsyncFrameProvider depthSource;     // depth frames provider (RFloat meters)
    [SerializeField] private IntrinsicProvider intrinsicProvider; // for intrinsics

    [Header("Compute")]
    [SerializeField] private ComputeShader splatCreator;         // SplatCreator.compute (CSMain)

    [Header("Radius Params (meters)")]
    [SerializeField] private float rScale = 1.0f;                // r = rScale * z / fx_px
    [SerializeField] private float rMin = 0.0005f;               // clamp lower bound
    [SerializeField] private float rMax = 0.05f;                 // clamp upper bound

    [Header("Debug")] 
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private bool useDebugCompute = false;
    [SerializeField] private string logPrefix = "[SplatManager]";

    public override Guid SplatGeneration => _currentSplat != null ? _currentSplat.JobId : Guid.Empty;

    private bool _hasIntrinsics;
    private float _fxPx, _fyPx, _cxPx, _cyPx;

    private int _kernel;
    private int _propDepthTex, _propPoints, _propW, _propH, _propFx, _propFy, _propCx, _propCy, _propRScale, _propRMin, _propRMax, _propValidCount;

    private Splat _currentSplat;

    private void OnEnable(){
        if (depthSource == null) throw new NullReferenceException("SplatManager: depthSource not assigned");
        if (intrinsicProvider == null) throw new NullReferenceException("SplatManager: intrinsicProvider not assigned");
        if (splatCreator == null) throw new NullReferenceException("SplatManager: splatCreator not assigned");

        _kernel = splatCreator.FindKernel("CSMain");
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

    private void OnDepthJobCompleted(AsyncFrame frame){

        if (!_hasIntrinsics) throw new InvalidOperationException("SplatManager: intrinsics not ready");
        if (frame.RenderTexture == null) throw new NullReferenceException("SplatManager: frame RenderTexture is null");

        var rt = frame.RenderTexture;
        int w = rt.width;
        int h = rt.height;

        if (w <= 0 || h <= 0) throw new InvalidOperationException("SplatManager: invalid depth size");

        // Scale intrinsics to depth texture resolution
        var intrinsics = intrinsicProvider.GetIntrinsics();
        var scaledIntrinsics = IntrinsicScaler.ScaleToOutput(intrinsics, w, h);

        // allocate points buffer (float4 per pixel)
        int count = w * h;
        int stride = sizeof(float) * 4;
        var points = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);

        // allocate valid count buffer (always needed for debug shader)
        var validCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
        validCount.SetData(new uint[]{0}); // initialize to 0

        // bind and dispatch
        splatCreator.SetTexture(_kernel, _propDepthTex, rt);
        splatCreator.SetBuffer(_kernel, _propPoints, points);
        splatCreator.SetBuffer(_kernel, _propValidCount, validCount);
        splatCreator.SetInt(_propW, w);
        splatCreator.SetInt(_propH, h);
        splatCreator.SetFloat(_propFx, scaledIntrinsics.fxPx);
        splatCreator.SetFloat(_propFy, scaledIntrinsics.fyPx);
        splatCreator.SetFloat(_propCx, scaledIntrinsics.cxPx);
        splatCreator.SetFloat(_propCy, scaledIntrinsics.cyPx);
        splatCreator.SetFloat(_propRScale, rScale);
        splatCreator.SetFloat(_propRMin, rMin);
        splatCreator.SetFloat(_propRMax, rMax);

        int gx = (w + 7) / 8;
        int gy = (h + 7) / 8;
        splatCreator.Dispatch(_kernel, gx, gy, 1);

        // read valid count for debug
        if (useDebugCompute){
            uint[] countData = new uint[1];
            validCount.GetData(countData);
            if (verboseLogging) Debug.Log($"{logPrefix} Valid points: {countData[0]} / {count}");
        }
        validCount.Dispose();

        // replace current splat
        if (_currentSplat != null){
            _currentSplat.Dispose();
            _currentSplat = null;
        }
        _currentSplat = new Splat(points, count, frame.Id);
        if (verboseLogging)
            Debug.Log($"{logPrefix} Splat ready id={frame.Id} count={count}");
        base.InvokeReady(_currentSplat);
    }

    private void OnDepthJobCanceled(Guid jobId){
        if (verboseLogging) Debug.Log($"{logPrefix} JobCanceled id={jobId}");
    }
}


