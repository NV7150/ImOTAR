using System;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class PcdCorrector : FrameProvider {
    [Header("Inputs")]
    [SerializeField] protected StructureManager structureManager;
    [SerializeField] protected PoseDiffManager poseDiff; // provides job-relative rotation/translation

    [Header("Output (meters)")]
    [SerializeField] protected RenderTexture outputMeters; // RFloat color + depth buffer

    [Header("Material")]
    [SerializeField] protected Material splatTransformMaterial; // SplatBaseTransform shader

    [Header("Intrinsics")]
    [SerializeField] protected IntrinsicProvider intrinsicProvider;
    [SerializeField] protected float nearMeters = 0.03f;
    [SerializeField] protected float farMeters = 20f;

    [Header("Debug")]
    [SerializeField] protected bool verboseLogging = true;
    [SerializeField] protected bool useDebugShader = false;
    [SerializeField] protected string logPrefix = "[SplatCorrector]";

    public override RenderTexture FrameTex => outputMeters;
    public override DateTime TimeStamp => _timestamp;

    private DateTime _timestamp;
    private bool _hasIntrinsics;
    private float _fxPx, _fyPx, _cxPx, _cyPx;
    private int _imgW, _imgH;

    protected PointCloud _currentSplat;
    protected Guid _lastCompletedJobId = Guid.Empty;

    private int _propPoints, _propFx, _propFy, _propCx, _propCy, _propW, _propH, _propR, _propT, _propProj, _propRenderMode, _propZTest;

    protected virtual void OnEnable(){
        if (structureManager == null) throw new NullReferenceException("SplatCorrector: splatManager not assigned");
        if (poseDiff == null) throw new NullReferenceException("SplatCorrector: poseDiff not assigned");
        if (splatTransformMaterial == null) throw new NullReferenceException("SplatCorrector: splatTransformMaterial not assigned");
        if (intrinsicProvider == null) throw new NullReferenceException("SplatCorrector: intrinsicProvider not assigned");
        if (outputMeters == null) throw new NullReferenceException("SplatCorrector: outputMeters not assigned");

        structureManager.OnReady += OnSplatReady;

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

    protected virtual void OnDisable(){
        if (structureManager != null) structureManager.OnReady -= OnSplatReady;
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
                throw new InvalidOperationException("SplatCorrector: outputMeters must be RFloat");
            if (outputMeters.depth == 0)
                throw new InvalidOperationException("SplatCorrector: outputMeters must have a depth buffer (24-bit recommended)");
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

    private void OnSplatReady(PointCloud splat){
        _currentSplat = splat;
        _lastCompletedJobId = splat.JobId;
        InitOutputOnce();
        if (verboseLogging) Debug.Log($"{logPrefix} Received Splat id={splat.JobId} count={splat.Count}");
    }

    protected virtual void LateUpdate(){
        if (!ShouldRun()) return;

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
            throw new InvalidOperationException("SplatCorrector: PointsBuffer invalid");

        if (!TryGetRelative(_currentSplat.JobId, out var relRotSC, out var relPos)){
            return;
        }

        var intrinsics = intrinsicProvider.GetIntrinsics();
        var scaledIntrinsics = IntrinsicScaler.ScaleToOutput(intrinsics, outputMeters.width, outputMeters.height);

        if (verboseLogging) {
            Debug.Log($"{logPrefix} Original intrinsics: fx={intrinsics.fxPx:F2} fy={intrinsics.fyPx:F2} cx={intrinsics.cxPx:F2} cy={intrinsics.cyPx:F2} res={intrinsics.width}x{intrinsics.height}");
            Debug.Log($"{logPrefix} Scaled intrinsics: fx={scaledIntrinsics.fxPx:F2} fy={scaledIntrinsics.fyPx:F2} cx={scaledIntrinsics.cxPx:F2} cy={scaledIntrinsics.cyPx:F2} res={scaledIntrinsics.width}x{scaledIntrinsics.height}");
        }

        int zTest = !SystemInfo.usesReversedZBuffer ? (int)CompareFunction.GreaterEqual : (int)CompareFunction.LessEqual;
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

        var active = RenderTexture.active;
        RenderTexture.active = outputMeters;
        float clearDepthValue = !SystemInfo.usesReversedZBuffer ? 0f : 1f;
        var cb = new CommandBuffer();
        cb.ClearRenderTarget(true, true, new Color(-1f, -1f, -1f, 1f), clearDepthValue);
        Graphics.ExecuteCommandBuffer(cb);
        cb.Release();
        int vertexCount = _currentSplat.Count * 6; // 2 triangles per quad
        if (verboseLogging) Debug.Log($"{logPrefix} Drawing {_currentSplat.Count} points, {vertexCount} vertices");

        splatTransformMaterial.SetInt(_propRenderMode, 0);
        splatTransformMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, vertexCount, 1);

        splatTransformMaterial.SetInt(_propRenderMode, 1);
        splatTransformMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, vertexCount, 1);
        RenderTexture.active = active;

        _timestamp = DateTime.Now;
        TickUp();

        if(verboseLogging)
            Debug.Log($"{logPrefix} Update Completed");

        AfterRender(relRotSC, relPos);
    }

    protected virtual bool TryGetRelative(Guid jobId, out Quaternion R, out Vector3 t){
        R = Quaternion.identity;
        t = Vector3.zero;
        if (poseDiff == null) return false;


        if (!poseDiff.TryGetDiffFrom(jobId, out t, out R)){
            if (verboseLogging) 
                Debug.LogWarning($"{logPrefix}: generation not found: {jobId}");
            return false;
        }
        EnsureOutputCreated();



        return true;
    }

    protected virtual void AfterRender(Quaternion R, Vector3 t){
        // No-op by default
    }

    protected virtual bool ShouldRun(){
        return true;
    }

    protected void EnsureOutputCreated(){
        if (outputMeters == null)
            throw new NullReferenceException("SplatCorrector: outputMeters not assigned");
        if (outputMeters.format != RenderTextureFormat.RFloat)
            throw new InvalidOperationException("SplatCorrector: outputMeters must be RFloat");
        if (outputMeters.depth == 0)
            throw new InvalidOperationException("SplatCorrector: outputMeters must have a depth buffer (24-bit recommended)");
        if (!outputMeters.IsCreated()) 
            outputMeters.Create();
    }
}


