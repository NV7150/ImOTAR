using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class StateSplatCorrector : FrameProvider {
    [Header("State")]
    [SerializeField] private StateManager state;

    [Header("Inputs")]
    [SerializeField] private SplatManager splatManager;
    [SerializeField] private AsyncFrameProvider depthSource; // retained for parity; events no longer used here
    [SerializeField] private PoseDiffManager poseDiff;       // job-relative diff (from ProcessStart)

    [Header("Output (meters)")]
    [SerializeField] private RenderTexture outputMeters;     // RFloat color + depth buffer

    [Header("Material")]
    [SerializeField] private Material splatTransformMaterial; // SplatBaseTransform shader

    [Header("Intrinsics")]
    [SerializeField] private IntrinsicProvider intrinsicProvider;
    [SerializeField] private float nearMeters = 0.03f;
    [SerializeField] private float farMeters = 20f;

    [Header("Holes & Rendering")]
    [SerializeField] private bool useNearestSampling = true;   // parity
    [SerializeField, Range(0f, 1f)] private float maxUvDispNormalized = 0.01f; // parity


    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private bool useDebugShader = false;
    [SerializeField] private string logPrefix = "[StateSplatCorrector]";

    public override RenderTexture FrameTex => outputMeters;
    public override DateTime TimeStamp => _timestamp;

    private DateTime _timestamp;
    private bool _hasIntrinsics;
    private float _fxPx, _fyPx, _cxPx, _cyPx;
    private int _imgW, _imgH;

    private Splat _currentSplat;
    private Guid _lastCompletedJobId = Guid.Empty;

    private int _propPoints, _propFx, _propFy, _propCx, _propCy, _propW, _propH, _propR, _propT, _propProj, _propRenderMode, _propZTest;

    private void OnEnable(){
        if (state == null) throw new NullReferenceException("StateSplatCorrector: state not assigned");
        if (splatManager == null) throw new NullReferenceException("StateSplatCorrector: splatManager not assigned");
        if (depthSource == null) throw new NullReferenceException("StateSplatCorrector: depthSource not assigned");
        if (poseDiff == null) throw new NullReferenceException("StateSplatCorrector: poseDiff not assigned");
        if (splatTransformMaterial == null) throw new NullReferenceException("StateSplatCorrector: splatTransformMaterial not assigned");
        if (intrinsicProvider == null) throw new NullReferenceException("StateSplatCorrector: intrinsicProvider not assigned");
        if (outputMeters == null) throw new NullReferenceException("StateSplatCorrector: outputMeters not assigned");

        splatManager.OnSplatReady += OnSplatReady;
        // No local baseline capture; SimpleMotionDiff handles it via provider events

        // Ensure output is created at startup (external resource, no size changes here)
        EnsureOutputCreated();

        _propPoints = Shader.PropertyToID("_Points");
        _propFx = Shader.PropertyToID("_FxPx");
        _propFy = Shader.PropertyToID("_FyPx");
        _propCx = Shader.PropertyToID("_CxPx");
        _propCy = Shader.PropertyToID("_CyPx");
        _propW  = Shader.PropertyToID("_Width");
        _propH  = Shader.PropertyToID("_Height");
        _propR  = Shader.PropertyToID("_R");
        _propT  = Shader.PropertyToID("_t");
        _propProj = Shader.PropertyToID("_Proj");
        _propRenderMode = Shader.PropertyToID("_RenderMode");
        _propZTest = Shader.PropertyToID("_ZTest");

        StartCoroutine(WaitForIntrinsics());
    }

    private void OnDisable(){
        if (splatManager != null) splatManager.OnSplatReady -= OnSplatReady;
    }

    private System.Collections.IEnumerator WaitForIntrinsics(){
        while (!_hasIntrinsics){
            TryInitIntrinsics();
            if (_hasIntrinsics) break;
            yield return null;
        }
        InitOutputOnce();
    }

    private void TryInitIntrinsics(){
        if (_hasIntrinsics) return;
        if (intrinsicProvider != null){
            var intrinsics = intrinsicProvider.GetIntrinsics();
            if (intrinsics.isValid){
                _imgW = intrinsics.width;
                _imgH = intrinsics.height;
                _fxPx = intrinsics.fxPx;
                _fyPx = intrinsics.fyPx;
                _cxPx = intrinsics.cxPx;
                _cyPx = intrinsics.cyPx;
                _hasIntrinsics = true;
                if (verboseLogging) Debug.Log($"{logPrefix} Intrinsics: fx={_fxPx:F2} fy={_fyPx:F2} cx={_cxPx:F2} cy={_cyPx:F2}");
            }
        }
    }

    private void InitOutputOnce(){
        if (!IsInitTexture){
            if (outputMeters.format != RenderTextureFormat.RFloat)
                throw new InvalidOperationException("StateSplatCorrector: outputMeters must be RFloat");
            if (outputMeters.depth == 0)
                throw new InvalidOperationException("StateSplatCorrector: outputMeters must have a depth buffer (24-bit recommended)");
            EnsureOutputCreated();
            outputMeters.wrapMode = TextureWrapMode.Clamp;
            outputMeters.filterMode = FilterMode.Bilinear;
            var active = RenderTexture.active;
            RenderTexture.active = outputMeters;
            GL.Clear(false, true, new Color(-1f, -1f, -1f, 1f));
            RenderTexture.active = active;
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
    }

    private void OnSplatReady(Splat splat){
        _currentSplat = splat;
        _lastCompletedJobId = splat.JobId;
        InitOutputOnce();
        if (verboseLogging) Debug.Log($"{logPrefix} Received Splat id={splat.JobId} count={splat.Count}");
    }

    // Job start/cancel handled by PoseDiffManager

    private void LateUpdate(){
        // State gate: only run when ALIVE to avoid any overhead in other states
        if (state.CurrState != State.ALIVE) return;

        if (!IsInitTexture){
            if (verboseLogging) Debug.Log($"{logPrefix} Early return: texture not initialized");
            return;
        }
        if (!_hasIntrinsics){
            if (verboseLogging) Debug.Log($"{logPrefix} Early return: intrinsics not ready");
            return;
        }
        if (_currentSplat == null){
            if (verboseLogging) Debug.Log($"{logPrefix} Early return: no current splat");
            return;
        }
        if (_currentSplat.PointsBuffer == null || !_currentSplat.PointsBuffer.IsValid())
            throw new InvalidOperationException("StateSplatCorrector: PointsBuffer invalid");

        // Ensure poseDiff has baseline for this job
        if (poseDiff.Generation == Guid.Empty || poseDiff.Generation != _currentSplat.JobId){
            if (verboseLogging) Debug.Log($"{logPrefix} Early return: diff baseline not ready or mismatched gen (diff={poseDiff.Generation}, job={_currentSplat.JobId})");
            return;
        }
        EnsureOutputCreated();
        Quaternion relRotSC = poseDiff.Rotation;
        Vector3 relPos = poseDiff.Translation;

        // Set uniforms with scaled intrinsics
        var intrinsics = intrinsicProvider.GetIntrinsics();
        var scaledIntrinsics = IntrinsicScaler.ScaleToOutput(intrinsics, outputMeters.width, outputMeters.height);

        if (verboseLogging) {
            Debug.Log($"{logPrefix} Original intrinsics: fx={intrinsics.fxPx:F2} fy={intrinsics.fyPx:F2} cx={intrinsics.cxPx:F2} cy={intrinsics.cyPx:F2} res={intrinsics.width}x{intrinsics.height}");
            Debug.Log($"{logPrefix} Scaled intrinsics: fx={scaledIntrinsics.fxPx:F2} fy={scaledIntrinsics.fyPx:F2} cx={scaledIntrinsics.cxPx:F2} cy={scaledIntrinsics.cyPx:F2} res={scaledIntrinsics.width}x{scaledIntrinsics.height}");
        }

        int zTest = !SystemInfo.usesReversedZBuffer ? (int)UnityEngine.Rendering.CompareFunction.GreaterEqual : (int)UnityEngine.Rendering.CompareFunction.LessEqual;
        splatTransformMaterial.SetInt(_propZTest, zTest);

        splatTransformMaterial.SetBuffer(_propPoints, _currentSplat.PointsBuffer);
        splatTransformMaterial.SetFloat(_propFx, scaledIntrinsics.fxPx);
        splatTransformMaterial.SetFloat(_propFy, scaledIntrinsics.fyPx);
        splatTransformMaterial.SetFloat(_propCx, scaledIntrinsics.cxPx);
        splatTransformMaterial.SetFloat(_propCy, scaledIntrinsics.cyPx);
        splatTransformMaterial.SetInt(_propW, outputMeters.width);
        splatTransformMaterial.SetInt(_propH, outputMeters.height);
        splatTransformMaterial.SetMatrix(_propR, Matrix4x4.Rotate(relRotSC));
        splatTransformMaterial.SetVector(_propT, relPos);
        splatTransformMaterial.SetMatrix(_propProj, IntrinsicScaler.BuildProjectionMatrix(scaledIntrinsics, outputMeters.width, outputMeters.height, nearMeters, farMeters));

        // Draw to output
        var active = RenderTexture.active;
        RenderTexture.active = outputMeters;
        float clearDepthValue = !SystemInfo.usesReversedZBuffer ? 0f : 1f;
        var cb = new CommandBuffer();
        cb.ClearRenderTarget(true, true, new Color(-1f, -1f, -1f, 1f), clearDepthValue);
        Graphics.ExecuteCommandBuffer(cb);
        cb.Release();
        int vertexCount = _currentSplat.Count * 6; // 2 triangles per quad
        if (verboseLogging) Debug.Log($"{logPrefix} Drawing {_currentSplat.Count} points, {vertexCount} vertices");

        // Pass 0: valid points
        splatTransformMaterial.SetInt(_propRenderMode, 0);
        splatTransformMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, vertexCount, 1);
        // Pass 1: holes (-1)
        splatTransformMaterial.SetInt(_propRenderMode, 1);
        splatTransformMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, vertexCount, 1);
        RenderTexture.active = active;

        _timestamp = DateTime.Now;
        TickUp();
    }

    private void EnsureOutputCreated(){
        if (outputMeters == null)
            throw new NullReferenceException("StateSplatCorrector: outputMeters not assigned");
        if (outputMeters.format != RenderTextureFormat.RFloat)
            throw new InvalidOperationException("StateSplatCorrector: outputMeters must be RFloat");
        if (outputMeters.depth == 0)
            throw new InvalidOperationException("StateSplatCorrector: outputMeters must have a depth buffer (24-bit recommended)");
        if (!outputMeters.IsCreated()) outputMeters.Create();
    }
}


