using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class SplatBaseCorrector : FrameProvider {
    [Header("Inputs")]
    [SerializeField] private StructureManager splatManager;
    [SerializeField] private AsyncFrameProvider depthSource; // for job start/cancel to record pose timing
    [SerializeField] private MotionObtain motionSource;      // for current pose and start pose capture
    [SerializeField] private Scheduler scheduler;            // for update requests

    [Header("Output (meters)")]
    [SerializeField] private RenderTexture outputMeters;     // RFloat color + depth buffer

    [Header("Material")]
    [SerializeField] private Material splatTransformMaterial; // SplatBaseTransform shader

    [Header("Intrinsics")]
    [SerializeField] private IntrinsicProvider intrinsicProvider;
    [SerializeField] private float nearMeters = 0.03f;
    [SerializeField] private float farMeters = 20f;

    [Header("Holes & Rendering")]
    [SerializeField] private bool useNearestSampling = true;   // reserved for parity; not used in this pass
    [SerializeField, Range(0f, 1f)] private float maxUvDispNormalized = 0.01f; // reserved for parity; not used

    [Header("Rotation Threshold (deg)")]
    [SerializeField] private bool useYaw = true;
    [SerializeField] private bool usePitch = false;
    [SerializeField] private bool useRoll = false;
    [SerializeField] private float updateRequestRotationThresholdDeg = 3f;

    [Header("Translation Threshold (meters)")]
    [SerializeField] private float updateRequestTranslationThresholdMeters = 0.02f;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private bool useDebugShader = false;
    [SerializeField] private string logPrefix = "[SplatBaseCorrector]";

    public override RenderTexture FrameTex => outputMeters;
    public override DateTime TimeStamp => _timestamp;

    private DateTime _timestamp;
    private bool _hasIntrinsics;
    private float _fxPx, _fyPx, _cxPx, _cyPx;
    private int _imgW, _imgH;

    private readonly Dictionary<Guid, Quaternion> _startPoseByJobId = new Dictionary<Guid, Quaternion>();
    private readonly Dictionary<Guid, Vector3> _startPosByJobId = new Dictionary<Guid, Vector3>();

    private PointCloud _currentSplat;
    private Guid _lastCompletedJobId = Guid.Empty;

    private int _propPoints, _propFx, _propFy, _propCx, _propCy, _propW, _propH, _propR, _propT, _propProj, _propRenderMode, _propZTest;

    private void OnEnable(){
        if (splatManager == null) throw new NullReferenceException("SplatBaseCorrector: splatManager not assigned");
        if (depthSource == null) throw new NullReferenceException("SplatBaseCorrector: depthSource not assigned");
        if (motionSource == null) throw new NullReferenceException("SplatBaseCorrector: motionSource not assigned");
        if (splatTransformMaterial == null) throw new NullReferenceException("SplatBaseCorrector: splatTransformMaterial not assigned");
        if (intrinsicProvider == null) throw new NullReferenceException("SplatBaseCorrector: intrinsicProvider not assigned");
        if (outputMeters == null) throw new NullReferenceException("SplatBaseCorrector: outputMeters not assigned");

        splatManager.OnReady += OnSplatReady;
        depthSource.OnAsyncFrameStarted += OnDepthJobStarted;
        depthSource.OnAsyncFrameCanceled += OnDepthJobCanceled;

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
        if (splatManager != null) splatManager.OnReady -= OnSplatReady;
        if (depthSource != null){
            depthSource.OnAsyncFrameStarted -= OnDepthJobStarted;
            depthSource.OnAsyncFrameCanceled -= OnDepthJobCanceled;
        }
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
                throw new InvalidOperationException("SplatBaseCorrector: outputMeters must be RFloat");
            if (outputMeters.depth == 0)
                throw new InvalidOperationException("SplatBaseCorrector: outputMeters must have a depth buffer (24-bit recommended)");
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

    private void OnSplatReady(PointCloud splat){
        _currentSplat = splat;
        _lastCompletedJobId = splat.JobId;
        InitOutputOnce();
        if (verboseLogging) Debug.Log($"{logPrefix} Received Splat id={splat.JobId} count={splat.Count}");
    }

    private void OnDepthJobStarted(Guid jobId){
        if (!motionSource.TryGetLatestData<AbsoluteRotationData>(out var rot))
            throw new InvalidOperationException("SplatBaseCorrector: rotation data unavailable at job start");
        if (!motionSource.TryGetLatestData<AbsolutePositionData>(out var pos))
            throw new InvalidOperationException("SplatBaseCorrector: position data unavailable at job start");
        _startPoseByJobId[jobId] = rot.Rotation;
        _startPosByJobId[jobId] = pos.Position;
    }

    private void OnDepthJobCanceled(Guid jobId){
        _startPoseByJobId.Remove(jobId);
        _startPosByJobId.Remove(jobId);
    }

    private void LateUpdate(){
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
            throw new InvalidOperationException("SplatBaseCorrector: PointsBuffer invalid");

        if (!motionSource.TryGetLatestData<AbsoluteRotationData>(out var rotAbs)){
            if (verboseLogging) Debug.Log($"{logPrefix} Early return: rotation data unavailable");
            return;
        }
        if (!motionSource.TryGetLatestData<AbsolutePositionData>(out var posAbs)){
            if (verboseLogging) Debug.Log($"{logPrefix} Early return: position data unavailable");
            return;
        }

        if (!_startPoseByJobId.TryGetValue(_currentSplat.JobId, out var poseAtStart)){
            if (verboseLogging) Debug.Log($"{logPrefix} Early return: start pose missing for job {_currentSplat.JobId}");
            return;
        }
        if (!_startPosByJobId.TryGetValue(_currentSplat.JobId, out var posAtStart)){
            if (verboseLogging) Debug.Log($"{logPrefix} Early return: start position missing for job {_currentSplat.JobId}");
            return;
        }

        // Compute source->current transform in current frame
        Quaternion R_wc0 = poseAtStart;
        Quaternion R_wc1 = rotAbs.Rotation;
        Quaternion relRotSC = Quaternion.Inverse(R_wc1) * R_wc0;
        Vector3 relPos = Quaternion.Inverse(R_wc1) * (posAtStart - posAbs.Position);

        // Set uniforms with scaled intrinsics
        var intrinsics = intrinsicProvider.GetIntrinsics();
        var scaledIntrinsics = IntrinsicScaler.ScaleToOutput(intrinsics, outputMeters.width, outputMeters.height);
        
        if (verboseLogging) {
            Debug.Log($"{logPrefix} Original intrinsics: fx={intrinsics.fxPx:F2} fy={intrinsics.fyPx:F2} cx={intrinsics.cxPx:F2} cy={intrinsics.cyPx:F2} res={intrinsics.width}x{intrinsics.height}");
            Debug.Log($"{logPrefix} Scaled intrinsics: fx={scaledIntrinsics.fxPx:F2} fy={scaledIntrinsics.fyPx:F2} cx={scaledIntrinsics.cxPx:F2} cy={scaledIntrinsics.cyPx:F2} res={scaledIntrinsics.width}x{scaledIntrinsics.height}");
        }
        
        // Set depth test mode according to platform's Z buffer convention
        // LEqual (4) for normal Z, GreaterEqual (7) for reversed Z
        // int zTest = SystemInfo.usesReversedZBuffer ? (int)UnityEngine.Rendering.CompareFunction.GreaterEqual : (int)UnityEngine.Rendering.CompareFunction.LessEqual;
        int zTest = !SystemInfo.usesReversedZBuffer ? (int)UnityEngine.Rendering.CompareFunction.GreaterEqual : (int)UnityEngine.Rendering.CompareFunction.LessEqual;
        splatTransformMaterial.SetInt(_propZTest, zTest);
        // --- Alternative (commented out): Spec-compliant ZTest for Reversed-Z ---
        // Use this if projection P is OpenGL-like before GL.GetGPUProjectionMatrix.
        // int zTestSpec = SystemInfo.usesReversedZBuffer
        //     ? (int)UnityEngine.Rendering.CompareFunction.GreaterEqual
        //     : (int)UnityEngine.Rendering.CompareFunction.LessEqual;
        // splatTransformMaterial.SetInt(_propZTest, zTestSpec);

        splatTransformMaterial.SetBuffer(_propPoints, _currentSplat.PointsBuffer);
        splatTransformMaterial.SetFloat(_propFx, scaledIntrinsics.fxPx);
        splatTransformMaterial.SetFloat(_propFy, scaledIntrinsics.fyPx);
        splatTransformMaterial.SetFloat(_propCx, scaledIntrinsics.cxPx);
        splatTransformMaterial.SetFloat(_propCy, scaledIntrinsics.cyPx);
        splatTransformMaterial.SetInt(_propW, outputMeters.width);
        splatTransformMaterial.SetInt(_propH, outputMeters.height);
        splatTransformMaterial.SetMatrix(_propR, Matrix4x4.Rotate(relRotSC));
        splatTransformMaterial.SetVector(_propT, relPos);
        // Use raw intrinsics here to avoid double scaling inside BuildProjectionMatrix
        splatTransformMaterial.SetMatrix(_propProj, IntrinsicScaler.BuildProjectionMatrix(intrinsics, outputMeters.width, outputMeters.height, nearMeters, farMeters));

        // Draw to output
        var active = RenderTexture.active;
        RenderTexture.active = outputMeters;
        // Clear color and depth explicitly (depth value depends on Z-buffer convention)
        // float clearDepthValue = SystemInfo.usesReversedZBuffer ? 0f : 1f;
        float clearDepthValue = !SystemInfo.usesReversedZBuffer ? 0f : 1f;
        var cb = new CommandBuffer();
        cb.ClearRenderTarget(true, true, new Color(-1f, -1f, -1f, 1f), clearDepthValue);
        // --- Alternative (commented out): Spec-compliant clear depth for Reversed-Z ---
        // float clearDepthSpec = SystemInfo.usesReversedZBuffer ? 0f : 1f;
        // var cb = new CommandBuffer();
        // cb.ClearRenderTarget(true, true, new Color(-1f, -1f, -1f, 1f), clearDepthSpec);
        Graphics.ExecuteCommandBuffer(cb);
        cb.Release();
        int vertexCount = _currentSplat.Count * 6; // 2 triangles per quad
        if (verboseLogging) Debug.Log($"{logPrefix} Drawing {_currentSplat.Count} points, {vertexCount} vertices");
        
        // Pass 0: valid points
        splatTransformMaterial.SetInt(_propRenderMode, 0);
        splatTransformMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, vertexCount, 1);
        // Pass 1: holes (-1) stamped in screen space using stored u,v; drawn with same pipeline
        splatTransformMaterial.SetInt(_propRenderMode, 1);
        splatTransformMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, vertexCount, 1);
        RenderTexture.active = active;

        // Update request policy (same as TransformCorrector)
        Quaternion relRotRC = Quaternion.Inverse(poseAtStart) * rotAbs.Rotation;
        bool req = false;
        if (updateRequestRotationThresholdDeg > 0f){
            Vector3 e = relRotRC.eulerAngles;
            float m = 0f;
            if (usePitch) m = Mathf.Max(m, Mathf.Abs(NormalizeDegrees(e.x)));
            if (useYaw)   m = Mathf.Max(m, Mathf.Abs(NormalizeDegrees(e.y)));
            if (useRoll)  m = Mathf.Max(m, Mathf.Abs(NormalizeDegrees(e.z)));
            if (m >= updateRequestRotationThresholdDeg) req = true;
        }
        if (!req && updateRequestTranslationThresholdMeters > 0f){
            float tm = relPos.magnitude;
            if (tm >= updateRequestTranslationThresholdMeters) req = true;
        }
        if (req && scheduler != null && _lastCompletedJobId != Guid.Empty){
            scheduler.RequestUpdate(_lastCompletedJobId);
        }

        _timestamp = DateTime.Now;
        TickUp();
    }

    private static float NormalizeDegrees(float deg){ return Mathf.Repeat(deg + 180f, 360f) - 180f; }
}


