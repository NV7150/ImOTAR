using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using System;

[DisallowMultipleComponent]
public class RotationCorrector : AsyncFrameProvider {
    [Header("Inputs")]
    [SerializeField] private MotionObtain motionSource;
    [SerializeField] private AsyncFrameProvider sourceProvider; // Completed source provider
    [SerializeField] private Scheduler scheduler;              // For update requests

    [Header("Output (this FrameProvider)")]
    [SerializeField] private RenderTexture rotatedMask;

    [Header("Material")] 
    [SerializeField] private Material imuRotationMaterial; // Assign ImOTAR/ImuRotationProjective

    [Header("Axis Mapping")]
    [SerializeField] private bool useYaw = true;
    [SerializeField] private bool usePitch = false;
    [SerializeField] private bool useRoll = false;

    [Header("AR Foundation")]
    [SerializeField] private ARCameraManager _arCameraManager;
    private bool _hasIntrinsics;
    private float _fxN, _fyN, _cxN, _cyN;
    // ARカメラの元解像度（intrinsicsの正規化元）
    private int _intrinsicWidth, _intrinsicHeight;
    private System.DateTime _timestamp;

    // Snapshot of last completed source and pose at job start
    private RenderTexture _capturedSource;
    private System.Collections.Generic.Dictionary<System.Guid, Quaternion> _startPoseByJobId = new System.Collections.Generic.Dictionary<System.Guid, Quaternion>();
    private Quaternion _sourcePoseAtStart = Quaternion.identity;
    private bool _hasSnapshot;
    private bool _warnedNoSnapshot;

    [Header("Update Request")]
    [SerializeField] private float updateRequestThresholdDeg = 3f;
    private System.Guid _lastCompletedJobId = System.Guid.Empty;
    private System.Collections.Generic.HashSet<System.Guid> _requestedUpdateIds = new System.Collections.Generic.HashSet<System.Guid>();

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[RotationCorrector]";

    [Header("Diagnostics")]
    [SerializeField] private bool logScaleDiagnostics = false;
    [SerializeField] private int logEveryNFrames = 30;
    private int _logFrameCounter = 0;

    public override RenderTexture FrameTex => rotatedMask;
    public override System.DateTime TimeStamp => _timestamp;

    private void Reset(){
        // Intentionally do not auto-create material; require explicit assignment to avoid silent fallback
    }

    private void OnEnable(){
        if (motionSource == null) throw new System.NullReferenceException("RotationCorrector: motionSource not assigned");
        if (sourceProvider == null) throw new System.NullReferenceException("RotationCorrector: sourceProvider not assigned");
        if (imuRotationMaterial == null) throw new System.NullReferenceException("RotationCorrector: imuRotationMaterial not assigned");
        if (rotatedMask == null) throw new System.NullReferenceException("RotationCorrector: rotatedMask not assigned");
        if (scheduler == null) throw new System.NullReferenceException("RotationCorrector: scheduler not assigned");

        // Subscribe to source async events
        sourceProvider.OnAsyncFrameStarted += OnSourceJobStarted;
        sourceProvider.OnAsyncFrameUpdated += OnSourceJobCompleted;
        sourceProvider.OnAsyncFrameCanceled += OnSourceJobCanceled;

        if (verboseLogging) Debug.Log($"{logPrefix} OnEnable: subscribed to source (material={(imuRotationMaterial!=null)}, rotMask={(rotatedMask!=null)})");

        StartCoroutine(WaitForIntrinsicsAndInit());
    }

    private void OnDisable(){
        if (sourceProvider != null){
            sourceProvider.OnAsyncFrameStarted -= OnSourceJobStarted;
            sourceProvider.OnAsyncFrameUpdated -= OnSourceJobCompleted;
            sourceProvider.OnAsyncFrameCanceled -= OnSourceJobCanceled;
        }
        if (verboseLogging) Debug.Log($"{logPrefix} OnDisable: unsubscribed");
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
                if (verboseLogging) Debug.Log($"{logPrefix} Intrinsics from AR: fx={_fxN:F6}, fy={_fyN:F6}, cx={_cxN:F6}, cy={_cyN:F6}");
            }
        }
    }

    private System.Collections.IEnumerator WaitForIntrinsicsAndInit(){
        if (verboseLogging) Debug.Log($"{logPrefix} Trying Obtain Intrinsic");
        while (!_hasIntrinsics){
            TryInitIntrinsics();
            if (_hasIntrinsics) break;
            yield return null;
        }
        if (verboseLogging) Debug.Log($"{logPrefix} Intrinsic Obtained");
        if (verboseLogging) Debug.Log($"{logPrefix} Intrinsics fx={_fxN:F6}, fy={_fyN:F6}, cx={_cxN:F6}, cy={_cyN:F6}");
        OnFirstOutputInit();
    }

    private static float ExtractYawDegrees(Quaternion q){
        Vector3 e = q.eulerAngles;
        return e.y;
    }
    private static float ExtractPitchDegrees(Quaternion q){
        Vector3 e = q.eulerAngles;
        return e.x;
    }
    private static float ExtractRollDegrees(Quaternion q){
        Vector3 e = q.eulerAngles;
        return e.z;
    }

    private void ValidateOutputRT(RenderTexture src){
        if (rotatedMask == null){
            if (verboseLogging) Debug.LogError($"{logPrefix} ValidateOutputRT: rotatedMask is null");
            throw new System.NullReferenceException("RotationCorrector: rotatedMask not assigned");
        }
        if (src == null){
            if (verboseLogging) Debug.LogError($"{logPrefix} ValidateOutputRT: src is null");
            throw new System.NullReferenceException("RotationCorrector: captured source is null");
        }
        if (rotatedMask.width != src.width || rotatedMask.height != src.height){
            if (verboseLogging) Debug.LogError($"{logPrefix} ValidateOutputRT: size mismatch dst=({rotatedMask.width}x{rotatedMask.height}) src=({src.width}x{src.height})");
            throw new System.InvalidOperationException("RotationCorrector: rotatedMask size must match source size");
        }
    }

    private void OnFirstOutputInit(){
        if (!IsInitTexture){
            // Ensure rotatedMask starts from a known state and sampling is stable
            rotatedMask.wrapMode = TextureWrapMode.Clamp;
            rotatedMask.filterMode = FilterMode.Bilinear;
            var active = RenderTexture.active;
            RenderTexture.active = rotatedMask;
            GL.Clear(false, true, new Color(-1f, -1f, -1f, 1f));
            RenderTexture.active = active;
            OnFrameTexInitialized();
            IsInitTexture = true;
            if (verboseLogging) Debug.Log($"{logPrefix} Output init: rotatedMask cleared and ready");
        }
    }

    


    private void Process(RenderTexture src){
        if (!_hasIntrinsics){
            if (verboseLogging) Debug.LogWarning($"{logPrefix} Skip Process: intrinsics not ready");
            return;
        }
        if (!motionSource.TryGetLatestData<AbsoluteRotationData>(out var absSample)){
            if (verboseLogging) Debug.LogWarning($"{logPrefix} Process: no absolute rotation available");
            return;
        }
        var abs = absSample.Rotation;
        // Projective warp expects mapping current->source: R = C0^-1 * C
        Quaternion r = Quaternion.Inverse(_sourcePoseAtStart) * abs;
        Matrix4x4 R = Matrix4x4.Rotate(r);
        Vector3 eul = r.eulerAngles;
        if (verboseLogging) Debug.Log($"{logPrefix} Process: relEuler pitch={eul.x:F3}, yaw={eul.y:F3}, roll={eul.z:F3}");

        imuRotationMaterial.SetMatrix("_R", R);
        // intrinsics はUV正規化済みなのでそのまま使用する
        int w = src.width;
        int h = src.height;
        float fxRt = _fxN;
        float fyRt = _fyN;
        float cxRt = _cxN;
        float cyRt = _cyN;

        if (logScaleDiagnostics){
            _logFrameCounter++;
            int mod = Mathf.Max(1, logEveryNFrames);
            if ((_logFrameCounter % mod) == 0){
                Debug.Log($"{logPrefix} ScaleDiag: src=({w}x{h}), dst=({rotatedMask.width}x{rotatedMask.height}), intrRes=({_intrinsicWidth}x{_intrinsicHeight}), fxN={_fxN:F6}, fyN={_fyN:F6}, cxN={_cxN:F6}, cyN={_cyN:F6}, fxRt={fxRt:F6}, fyRt={fyRt:F6}, cxRt={cxRt:F6}, cyRt={cyRt:F6}");
            }
        }
        imuRotationMaterial.SetFloat("_Fx", fxRt);
        imuRotationMaterial.SetFloat("_Fy", fyRt);
        imuRotationMaterial.SetFloat("_Cx", cxRt);
        imuRotationMaterial.SetFloat("_Cy", cyRt);
        imuRotationMaterial.SetTexture("_MainTex", src);
        Graphics.Blit(src, rotatedMask, imuRotationMaterial, 0);
        if (verboseLogging) Debug.Log($"{logPrefix} Blit done: src=({src.width}x{src.height}) -> dst=({rotatedMask.width}x{rotatedMask.height})");

        // Threshold-based update request via Scheduler
        if (scheduler != null && _lastCompletedJobId != System.Guid.Empty) {
            float corr = ComputeCorrectionMagnitudeDegrees(r);
            if (corr >= updateRequestThresholdDeg && !_requestedUpdateIds.Contains(_lastCompletedJobId)){
                scheduler.RequestUpdate(_lastCompletedJobId);
                _requestedUpdateIds.Add(_lastCompletedJobId);
                if (verboseLogging) Debug.Log($"{logPrefix} RequestUpdate: id={_lastCompletedJobId}, corr={corr:F2} deg");
            }
        }
    }

    private void LateUpdate(){
        if (!IsInitTexture){
            if (verboseLogging) Debug.Log($"{logPrefix} Skip: not initialized");
            return;
        }
        if (!_hasSnapshot || _capturedSource == null){
            if (verboseLogging) Debug.Log($"{logPrefix} Skip: no snapshot (hasSnap={_hasSnapshot}, srcNull={_capturedSource==null})");
            return;
        }
        ValidateOutputRT(_capturedSource);
        Process(_capturedSource);
        _timestamp = System.DateTime.Now;
        TickUp();
    }

    // ---- Source async event handlers ----
    private void OnSourceJobStarted(System.Guid jobId){
        Quaternion pose = Quaternion.identity;
        if (motionSource != null && motionSource.TryGetLatestData<AbsoluteRotationData>(out var startSample)){
            pose = startSample.Rotation;
        }
        _startPoseByJobId[jobId] = pose;
        if (verboseLogging) {
            var e = pose.eulerAngles;
            Debug.Log($"{logPrefix} JobStarted: id={jobId}, startEuler=({e.x:F2},{e.y:F2},{e.z:F2})");
        }
    }

    private void OnSourceJobCompleted(AsyncFrame frame){
        var src = frame.RenderTexture;
        if (src == null) throw new System.NullReferenceException($"{logPrefix} JobCompleted: RenderTexture is null for id={frame.Id}");
        if (!_startPoseByJobId.TryGetValue(frame.Id, out var poseAtStart))
            throw new System.InvalidOperationException($"{logPrefix} JobCompleted: start pose missing for id={frame.Id}");
        EnsureCapturedSource(src);
        Graphics.CopyTexture(src, _capturedSource);
        _hasSnapshot = true;
        _warnedNoSnapshot = false;
        _sourcePoseAtStart = poseAtStart;
        _lastCompletedJobId = frame.Id;
        if (verboseLogging) Debug.Log($"{logPrefix} JobCompleted: id={frame.Id}, snap=({_capturedSource.width}x{_capturedSource.height}) captured");
        _startPoseByJobId.Remove(frame.Id);
        OnFirstOutputInit();
    }

    private void OnSourceJobCanceled(System.Guid jobId){
        _startPoseByJobId.Remove(jobId);
        if (verboseLogging) Debug.Log($"{logPrefix} JobCanceled: id={jobId}");
    }

    private void EnsureCapturedSource(RenderTexture src){
        if (_capturedSource != null && _capturedSource.width == src.width && _capturedSource.height == src.height)
        {
            if (verboseLogging) Debug.Log($"{logPrefix} Reuse snapshot RT: {src.width}x{src.height}");
            return;
        }
        if (_capturedSource != null){
            if (_capturedSource.IsCreated()) _capturedSource.Release();
            UnityEngine.Object.Destroy(_capturedSource);
            _capturedSource = null;
        }
        var desc = src.descriptor;
        desc.depthBufferBits = 0;
        desc.useMipMap = false;
        desc.autoGenerateMips = false;
        desc.enableRandomWrite = false;
        _capturedSource = new RenderTexture(desc){
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        _capturedSource.Create();
        if (verboseLogging) Debug.Log($"{logPrefix} Alloc snapshot RT: {desc.width}x{desc.height} {desc.graphicsFormat}");
    }

    private float ComputeCorrectionMagnitudeDegrees(Quaternion rel){
        Vector3 e = rel.eulerAngles;
        float m = 0f;
        if (usePitch) m = Mathf.Max(m, Mathf.Abs(NormalizeDegrees(e.x)));
        if (useYaw)   m = Mathf.Max(m, Mathf.Abs(NormalizeDegrees(e.y)));
        if (useRoll)  m = Mathf.Max(m, Mathf.Abs(NormalizeDegrees(e.z)));
        return m;
    }

    private static float NormalizeDegrees(float deg){
        // map [0,360) to [-180,180)
        deg = Mathf.Repeat(deg + 180f, 360f) - 180f;
        return deg;
    }
}


