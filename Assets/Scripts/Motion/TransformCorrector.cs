using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using System;
using System.Collections.Generic;

[DisallowMultipleComponent]
public class TransformCorrector : AsyncFrameProvider {
    [Header("Inputs")]
    [SerializeField] private MotionObtain motionSource;
    [SerializeField] private AsyncFrameProvider sourceProvider; // Previous PromptDA (or base) output provider
    [SerializeField] private Scheduler scheduler;               // For update requests

    [Header("Depth Input (meters)")]
    [SerializeField] private RenderTexture lowResDepthMeters;   // Low-res depth RT (meters)

    [Header("Output (this FrameProvider)")]
    [SerializeField] private RenderTexture transformedMask;     // Final output (same size as source)

    [Header("Material")] 
    [SerializeField] private Material se3BackwarpMaterial;      // ImOTAR/TranslateProjective (SE3 backwarp)

    [Header("Intrinsics (normalized)")]
    [SerializeField] private ARCameraManager _arCameraManager;  // Auto from AR

    [Header("Rotation Threshold (deg)")]
    [SerializeField] private bool useYaw = true;
    [SerializeField] private bool usePitch = false;
    [SerializeField] private bool useRoll = false;
    [SerializeField] private float updateRequestRotationThresholdDeg = 3f;

    [Header("Translation Threshold (meters)")]
    [SerializeField] private float updateRequestTranslationThresholdMeters = 0.02f;

    [Header("Debug")] 
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[TransformCorrector-SE3]";

    // Internal
    private bool _hasIntrinsics;
    private float _fxN, _fyN, _cxN, _cyN;
    private int _intrinsicWidth, _intrinsicHeight;
    private DateTime _timestamp;

    private RenderTexture _capturedSource; // previous source snapshot
    private Dictionary<Guid, Quaternion> _startPoseByJobId = new Dictionary<Guid, Quaternion>();
    private Dictionary<Guid, Vector3> _startPosByJobId = new Dictionary<Guid, Vector3>();
    private Quaternion _sourcePoseAtStart = Quaternion.identity;
    private Vector3 _sourcePosAtStart = Vector3.zero;
    private bool _hasSnapshot;

    private RenderTexture _upscaledDepth; // internal upscaled depth to output size

    public override RenderTexture FrameTex => transformedMask;
    public override DateTime TimeStamp => _timestamp;

    private void OnEnable(){
        if (motionSource == null) throw new NullReferenceException("TransformCorrector: motionSource not assigned");
        if (sourceProvider == null) throw new NullReferenceException("TransformCorrector: sourceProvider not assigned");
        if (se3BackwarpMaterial == null) throw new NullReferenceException("TransformCorrector: se3BackwarpMaterial not assigned");
        if (transformedMask == null) throw new NullReferenceException("TransformCorrector: transformedMask not assigned");
        if (scheduler == null) throw new NullReferenceException("TransformCorrector: scheduler not assigned");

        sourceProvider.OnAsyncFrameStarted += OnSourceJobStarted;
        sourceProvider.OnAsyncFrameUpdated += OnSourceJobCompleted;
        sourceProvider.OnAsyncFrameCanceled += OnSourceJobCanceled;

        StartCoroutine(WaitForIntrinsics());
    }

    private void OnDisable(){
        if (sourceProvider != null){
            sourceProvider.OnAsyncFrameStarted -= OnSourceJobStarted;
            sourceProvider.OnAsyncFrameUpdated -= OnSourceJobCompleted;
            sourceProvider.OnAsyncFrameCanceled -= OnSourceJobCanceled;
        }
        ReleaseRT(ref _upscaledDepth);
        ReleaseRT(ref _capturedSource);
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
        if (_arCameraManager != null && _arCameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr)){
            var res = intr.resolution;
            if (res.x > 0 && res.y > 0){
                _intrinsicWidth = res.x;
                _intrinsicHeight = res.y;
                _fxN = intr.focalLength.x / (float)res.x;
                _fyN = intr.focalLength.y / (float)res.y;
                _cxN = intr.principalPoint.x / (float)res.x;
                _cyN = intr.principalPoint.y / (float)res.y;
                _hasIntrinsics = true;
                if (verboseLogging) Debug.Log($"{logPrefix} Intrinsics: fx={_fxN:F6} fy={_fyN:F6} cx={_cxN:F6} cy={_cyN:F6}");
            }
        }
    }

    private void InitOutputOnce(){
        if (!IsInitTexture){
            transformedMask.wrapMode = TextureWrapMode.Clamp;
            transformedMask.filterMode = FilterMode.Bilinear;
            var active = RenderTexture.active;
            RenderTexture.active = transformedMask;
            GL.Clear(false, true, new Color(-1f, -1f, -1f, 1f));
            RenderTexture.active = active;
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
    }

    private void LateUpdate(){
        if (!IsInitTexture || !_hasSnapshot || _capturedSource == null) return;
        if (!_hasIntrinsics) return;
        if (lowResDepthMeters == null){
            if (verboseLogging) Debug.LogWarning($"{logPrefix} Skip: lowResDepthMeters not assigned");
            return;
        }
        EnsureUpscaledDepth(transformedMask.width, transformedMask.height);
        // Bilinear upscale: simple blit
        Graphics.Blit(lowResDepthMeters, _upscaledDepth);

        ValidateOutputRT(_capturedSource);
        ProcessSE3Backwarp(_capturedSource, _upscaledDepth);
        _timestamp = DateTime.Now;
        TickUp();
    }

    private void ProcessSE3Backwarp(RenderTexture srcPrev, RenderTexture depthCurUpscaled){
        if (!motionSource.TryGetLatestData<AbsoluteRotationData>(out var rotAbs)) return;
        if (!motionSource.TryGetLatestData<AbsolutePositionData>(out var posAbs)) return;

        // RotationCorrector 準拠の相対回転（しきい値・ログ用）
        Quaternion relRotRC = Quaternion.Inverse(_sourcePoseAtStart) * rotAbs.Rotation;

        // バックワープ用の source->current 変換（current座標系）
        Quaternion R_wc0 = _sourcePoseAtStart;        // source -> world
        Quaternion R_wc1 = rotAbs.Rotation;           // current -> world
        Quaternion relRotSC = Quaternion.Inverse(R_wc1) * R_wc0; // source->current in current frame
        Vector3     relPos   = Quaternion.Inverse(R_wc1) * (_sourcePosAtStart - posAbs.Position);

        // Set SE3 params
        Matrix4x4 R = Matrix4x4.Rotate(relRotSC);
        se3BackwarpMaterial.SetMatrix("_R", R);
        se3BackwarpMaterial.SetVector("_t", relPos);
        se3BackwarpMaterial.SetFloat("_Fx", _fxN);
        se3BackwarpMaterial.SetFloat("_Fy", _fyN);
        se3BackwarpMaterial.SetFloat("_Cx", _cxN);
        se3BackwarpMaterial.SetFloat("_Cy", _cyN);
        se3BackwarpMaterial.SetTexture("_MainTex", srcPrev);
        se3BackwarpMaterial.SetTexture("_DepthTex", depthCurUpscaled);

        // No display transform: inputs are assumed already screen-aligned with no extra flips

        Graphics.Blit(srcPrev, transformedMask, se3BackwarpMaterial, 0);

        // Update request policy
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
    }

    private void EnsureUpscaledDepth(int w, int h){
        if (_upscaledDepth != null && _upscaledDepth.width == w && _upscaledDepth.height == h) return;
        ReleaseRT(ref _upscaledDepth);
        _upscaledDepth = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat){
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false
        };
        _upscaledDepth.Create();
    }

    private void ValidateOutputRT(RenderTexture src){
        if (transformedMask.width != src.width || transformedMask.height != src.height){
            throw new InvalidOperationException("TransformCorrector: transformedMask size must match source size");
        }
    }

    // ---- Source async event handlers ----
    private Guid _lastCompletedJobId = Guid.Empty;

    private void OnSourceJobStarted(Guid jobId){
        Quaternion pose = Quaternion.identity;
        Vector3 pos = Vector3.zero;
        if (motionSource != null){
            if (motionSource.TryGetLatestData<AbsoluteRotationData>(out var rot)) pose = rot.Rotation;
            if (motionSource.TryGetLatestData<AbsolutePositionData>(out var p)) pos = p.Position;
        }
        _startPoseByJobId[jobId] = pose;
        _startPosByJobId[jobId] = pos;
    }

    private void OnSourceJobCompleted(AsyncFrame frame){
        var src = frame.RenderTexture;
        if (src == null) throw new NullReferenceException($"{logPrefix} JobCompleted: RenderTexture is null for id={frame.Id}");
        if (!_startPoseByJobId.TryGetValue(frame.Id, out var poseAtStart))
            throw new InvalidOperationException($"{logPrefix} JobCompleted: start pose missing for id={frame.Id}");
        if (!_startPosByJobId.TryGetValue(frame.Id, out var posAtStart))
            throw new InvalidOperationException($"{logPrefix} JobCompleted: start position missing for id={frame.Id}");

        EnsureCapturedSource(src);
        Graphics.CopyTexture(src, _capturedSource);
        _hasSnapshot = true;
        _sourcePoseAtStart = poseAtStart;
        _sourcePosAtStart = posAtStart;
        _lastCompletedJobId = frame.Id;
        InitOutputOnce();
    }

    private void OnSourceJobCanceled(Guid jobId){
        _startPoseByJobId.Remove(jobId);
        _startPosByJobId.Remove(jobId);
    }

    private void EnsureCapturedSource(RenderTexture src){
        if (_capturedSource != null && _capturedSource.width == src.width && _capturedSource.height == src.height) return;
        ReleaseRT(ref _capturedSource);
        var desc = src.descriptor; desc.depthBufferBits = 0; desc.useMipMap = false; desc.autoGenerateMips = false; desc.enableRandomWrite = false;
        _capturedSource = new RenderTexture(desc){ wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        _capturedSource.Create();
    }

    private static float NormalizeDegrees(float deg){ return Mathf.Repeat(deg + 180f, 360f) - 180f; }

    private static void ReleaseRT(ref RenderTexture rt){
        if (rt == null) return;
        if (rt.IsCreated()) rt.Release();
        UnityEngine.Object.Destroy(rt);
        rt = null;
    }
}


