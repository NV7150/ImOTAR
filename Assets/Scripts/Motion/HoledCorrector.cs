using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using System;
using System.Collections.Generic;

[DisallowMultipleComponent]
public class HoledCorrector : AsyncFrameProvider {
    [Header("Inputs")]
    [SerializeField] private MotionObtain motionSource;
    [SerializeField] private AsyncFrameProvider sourceProvider; // Previous PromptDA (or base) output provider
    [SerializeField] private Scheduler scheduler;               // For update requests

    [Header("Depth Input (meters)")]
    [SerializeField] private RenderTexture lowResDepthMeters;   // Real-time depth (low-res, meters)

    [Header("Output (this FrameProvider)")]
    [SerializeField] private RenderTexture transformedMask;     // Final output (holes=-1)

    [Header("Compute")] 
    [SerializeField] private ComputeShader fowardTransformCS;   // FowardTransform.compute

    [Header("Intrinsics (normalized)")]
    [SerializeField] private ARCameraManager _arCameraManager;  

    [Header("Update Thresholds")] 
    [SerializeField] private bool useYaw = true;
    [SerializeField] private bool usePitch = false;
    [SerializeField] private bool useRoll = false;
    [SerializeField] private float updateRequestRotationThresholdDeg = 3f;
    [SerializeField] private float updateRequestTranslationThresholdMeters = 0.02f;

    [Header("Debug")] 
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[HoledCorrector-FWD]";

    // Internal
    private bool _hasIntrinsics;
    private float _fxN, _fyN, _cxN, _cyN;
    private int _intrinsicWidth, _intrinsicHeight;
    private DateTime _timestamp;

    private RenderTexture _capturedSource; // source-time mask snapshot
    private RenderTexture _capturedDepth;  // source-time depth snapshot (meters, upscaled to source size)
    private bool _hasSnapshot;

    // Compute resources
    private int _kClear, _kMinDepth, _kResolve;
    private RenderTexture _outUav; // internal UAV if needed
    private ComputeBuffer _zBuf;   // Structured uint buffer (size = dstW*dstH)

    public override RenderTexture FrameTex => transformedMask;
    public override DateTime TimeStamp => _timestamp;

    private void OnEnable(){
        if (motionSource == null) throw new NullReferenceException("HoledCorrector: motionSource not assigned");
        if (sourceProvider == null) throw new NullReferenceException("HoledCorrector: sourceProvider not assigned");
        if (fowardTransformCS == null) throw new NullReferenceException("HoledCorrector: fowardTransformCS not assigned");
        if (transformedMask == null) throw new NullReferenceException("HoledCorrector: transformedMask not assigned");
        if (scheduler == null) throw new NullReferenceException("HoledCorrector: scheduler not assigned");

        _kClear    = fowardTransformCS.FindKernel("KClear");
        _kMinDepth = fowardTransformCS.FindKernel("KMinDepth");
        _kResolve  = fowardTransformCS.FindKernel("KResolve");

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
        ReleaseRT(ref _capturedSource);
        ReleaseRT(ref _capturedDepth);
        ReleaseRT(ref _outUav);
        ReleaseBuffer(ref _zBuf);
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
            // Initialize with -1
            var active = RenderTexture.active;
            RenderTexture.active = transformedMask;
            GL.Clear(false, true, new Color(-1f, -1f, -1f, 1f));
            RenderTexture.active = active;
            OnFrameTexInitialized();
            IsInitTexture = true;
        }
    }

    private void LateUpdate(){
        if (!IsInitTexture || !_hasSnapshot || _capturedSource == null || _capturedDepth == null) return;
        if (!_hasIntrinsics) return;
        ValidateOutputRT(_capturedSource);
        EnsureInternalTargets(transformedMask.width, transformedMask.height);
        ProcessForwardWarp(_capturedSource, _capturedDepth);
        _timestamp = DateTime.Now;
        TickUp();
    }

    private void ProcessForwardWarp(RenderTexture srcAtStart, RenderTexture depthAtStart){
        if (!motionSource.TryGetLatestData<AbsoluteRotationData>(out var rotAbs)) return;
        if (!motionSource.TryGetLatestData<AbsolutePositionData>(out var posAbs)) return;

        // source->current transform in current frame
        Quaternion R_wc0 = _sourcePoseAtStart;        // source -> world
        Quaternion R_wc1 = rotAbs.Rotation;           // current -> world
        Quaternion relRotSC = Quaternion.Inverse(R_wc1) * R_wc0; // source->current in current frame
        Vector3     relPos   = Quaternion.Inverse(R_wc1) * (_sourcePosAtStart - posAbs.Position);

        int dstW = transformedMask.width;
        int dstH = transformedMask.height;
        int srcW = srcAtStart.width;
        int srcH = srcAtStart.height;

        // Clear pass (dst domain)
        fowardTransformCS.SetInt("_DstWidth", dstW);
        fowardTransformCS.SetInt("_DstHeight", dstH);
        fowardTransformCS.SetTexture(_kClear, "_OutTex", GetOutUav());
        fowardTransformCS.SetBuffer(_kClear, "_ZBuf", _zBuf);
        Dispatch(_kClear, dstW, dstH);

        // Common params
        fowardTransformCS.SetInt("_SrcWidth", srcW);
        fowardTransformCS.SetInt("_SrcHeight", srcH);
        fowardTransformCS.SetInt("_DstWidth", dstW);
        fowardTransformCS.SetInt("_DstHeight", dstH);
        fowardTransformCS.SetFloat("_Fx", _fxN);
        fowardTransformCS.SetFloat("_Fy", _fyN);
        fowardTransformCS.SetFloat("_Cx", _cxN);
        fowardTransformCS.SetFloat("_Cy", _cyN);
        fowardTransformCS.SetMatrix("_R", Matrix4x4.Rotate(relRotSC));
        fowardTransformCS.SetVector("_t", relPos);

        // MinDepth pass (src domain)
        fowardTransformCS.SetTexture(_kMinDepth, "_DepthSrc", depthAtStart);
        fowardTransformCS.SetBuffer(_kMinDepth, "_ZBuf", _zBuf);
        Dispatch(_kMinDepth, srcW, srcH);

        // Resolve pass (src domain)
        fowardTransformCS.SetTexture(_kResolve, "_DepthSrc", depthAtStart);
        fowardTransformCS.SetTexture(_kResolve, "_SrcTex", srcAtStart);
        fowardTransformCS.SetBuffer(_kResolve, "_ZBuf", _zBuf);
        fowardTransformCS.SetTexture(_kResolve, "_OutTex", GetOutUav());
        Dispatch(_kResolve, srcW, srcH);

        // Copy to provided output if needed
        if (_outUav != null && transformedMask != _outUav){
            Graphics.Blit(_outUav, transformedMask);
        }

        // Update request policy (same as TransformCorrector)
        Quaternion relRotRC = Quaternion.Inverse(_sourcePoseAtStart) * rotAbs.Rotation;
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

    private void EnsureInternalTargets(int w, int h){
        // Output UAV
        bool needUav = transformedMask == null || !transformedMask.enableRandomWrite;
        if (!needUav){
            // Ensure sizes match
            if (transformedMask.width != w || transformedMask.height != h){
                throw new InvalidOperationException("HoledCorrector: transformedMask size must match source size");
            }
            ReleaseRT(ref _outUav);
        } else {
            if (_outUav == null || _outUav.width != w || _outUav.height != h){
                ReleaseRT(ref _outUav);
                _outUav = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat){
                    enableRandomWrite = true,
                    useMipMap = false,
                    autoGenerateMips = false,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                _outUav.Create();
            }
        }

        // Z buffer (structured uint buffer)
        int needed = w * h;
        if (_zBuf == null || _zBuf.count != needed){
            ReleaseBuffer(ref _zBuf);
            _zBuf = new ComputeBuffer(needed, sizeof(uint), ComputeBufferType.Structured);
        }
    }

    private RenderTexture GetOutUav(){
        return (transformedMask != null && transformedMask.enableRandomWrite) ? transformedMask : _outUav;
    }

    private void Dispatch(int kernel, int w, int h){
        int gx = Mathf.CeilToInt(w / 16.0f);
        int gy = Mathf.CeilToInt(h / 16.0f);
        fowardTransformCS.Dispatch(kernel, gx, gy, 1);
    }

    private void ValidateOutputRT(RenderTexture src){
        if (transformedMask.width != src.width || transformedMask.height != src.height){
            throw new InvalidOperationException("HoledCorrector: transformedMask size must match source size");
        }
    }

    // ---- Source async event handlers ----
    private Guid _lastCompletedJobId = Guid.Empty;
    private Quaternion _sourcePoseAtStart = Quaternion.identity;
    private Vector3 _sourcePosAtStart = Vector3.zero;

    private Dictionary<Guid, Quaternion> _startPoseByJobId = new Dictionary<Guid, Quaternion>();
    private Dictionary<Guid, Vector3> _startPosByJobId = new Dictionary<Guid, Vector3>();

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

        // Snapshot depth at source time: upscale to source size and store
        if (lowResDepthMeters == null)
            throw new NullReferenceException($"{logPrefix} JobCompleted: lowResDepthMeters not assigned");
        EnsureCapturedDepth(src.width, src.height);
        Graphics.Blit(lowResDepthMeters, _capturedDepth);

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

    private void EnsureCapturedDepth(int w, int h){
        if (_capturedDepth != null && _capturedDepth.width == w && _capturedDepth.height == h) return;
        ReleaseRT(ref _capturedDepth);
        _capturedDepth = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat){
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false,
            enableRandomWrite = false
        };
        _capturedDepth.Create();
    }

    private static float NormalizeDegrees(float deg){ return Mathf.Repeat(deg + 180f, 360f) - 180f; }

    private static void ReleaseRT(ref RenderTexture rt){
        if (rt == null) return;
        if (rt.IsCreated()) rt.Release();
        rt = null;
    }

    private static void ReleaseBuffer(ref ComputeBuffer buf){
        if (buf == null) return;
        buf.Release();
        buf = null;
    }
}


