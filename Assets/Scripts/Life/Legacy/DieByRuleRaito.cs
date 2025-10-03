using System;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class DieByRuleRaito : MonoBehaviour {
    [Header("Inputs")]
    [SerializeField] private PoseDiffManager poseDiff; // use diffs from last model update
    [SerializeField] private FrameProvider dieInput; // RFloat RT, holes: value < 0
    [SerializeField] private StateManager state;

    [Header("Thresholds")]
    [SerializeField, Range(0f, 1f)] private float unknownRatioThresh = 0.35f;
    [SerializeField, Min(0f)] private float rotDieDeg = 5.0f;
    [SerializeField, Min(0f)] private float posDieMeters = 0.03f;
    [SerializeField, Min(1)] private int coverageAvgWindow = 5; // EMA-like window via smoothing
    [SerializeField, Range(0f,1f)] private float coverageSmooth = 0.3f;

    [Header("Compute (Hole Count)")]
    [SerializeField] private ComputeShader holeCountCS; // Kernel: CSMain

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[DieByRule]";

    // GPU resources
    private int _kernel;
    private int _propTex, _propW, _propH, _propOut;
    private GraphicsBuffer _counter;
    private uint[] _cpuCounter = new uint[2]; // [0]=invalidCount, [1]=total

    // Smoothed unknown ratio
    private float _emaUnknownRatio;
    private bool _emaInit;

    // Async readback management
    private bool _requestInFlight;
    private int _lastDispatchW;
    private int _lastDispatchH;
    private bool _hasLatestUnknown;
    private float _latestUnknownRatio;
    private Guid _requestGeneration;   // generation captured at dispatch

    // Debug getters (read-only)
    public float RotDieDeg => rotDieDeg;
    public float PosDieMeters => posDieMeters;
    public float UnknownRatioThresh => unknownRatioThresh;
    public float EmaUnknownRatio => _emaUnknownRatio;
    public bool RequestInFlight => _requestInFlight;
    public bool HasLatestUnknown => _hasLatestUnknown;

    private void OnEnable(){
        if (poseDiff == null) throw new NullReferenceException("DieByRule: poseDiff not assigned");
        if (dieInput == null) throw new NullReferenceException("DieByRule: dieInput not assigned");
        if (state == null) throw new NullReferenceException("DieByRule: state not assigned");
        if (holeCountCS == null) throw new NullReferenceException("DieByRule: holeCountCS not assigned");

        _kernel = holeCountCS.FindKernel("CSMain");
        _propTex = Shader.PropertyToID("_Tex");
        _propW   = Shader.PropertyToID("_Width");
        _propH   = Shader.PropertyToID("_Height");
        _propOut = Shader.PropertyToID("_Out");

        state.OnGenerateEnd += OnBirthEnd;
        state.OnDiscard     += OnDeadInternal;

        AllocateCounter();
        _emaUnknownRatio = 0f;
        _emaInit = false;
        _requestInFlight = false;
        _hasLatestUnknown = false;
        _requestGeneration = Guid.Empty;
    }

    private void OnDisable(){
        state.OnGenerateEnd -= OnBirthEnd;
        state.OnDiscard     -= OnDeadInternal;
        ReleaseCounter();
    }

    private void AllocateCounter(){
        ReleaseCounter();
        _counter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, sizeof(uint));
        _counter.SetData(new uint[]{0u, 0u});
    }

    private void ReleaseCounter(){
        _counter?.Dispose();
        _counter = null;
    }

    private void OnBirthEnd(){
        // Reset smoothers and async state; baseline is handled by poseDiff (generation changes via provider)
        _emaInit = false;
        _hasLatestUnknown = false;
        _requestInFlight = false;
        if (logVerbose) Debug.Log($"{logPrefix} BirthEnd: reset coverage smoothing and async state");
    }

    private void OnDeadInternal(){
        // reset coverage smoother when leaving ALIVE
        _emaInit = false;
        _hasLatestUnknown = false;
        _requestInFlight = false;
    }

    private void Update(){
        if (state.CurrState != State.ACTIVE) return;

        // 1) Motion displacement vs baseline (poseDiff generation-based)
        if (poseDiff.Generation != Guid.Empty){
            float ang = Quaternion.Angle(Quaternion.identity, poseDiff.Rotation);
            float dist = poseDiff.Translation.magnitude;
            bool dieMotion = (ang >= rotDieDeg) || (dist >= posDieMeters);
            if (dieMotion){
                if (logVerbose) Debug.Log($"{logPrefix} DIE by motion: ang={ang:F2} deg, dist={dist:F3} m");
                state.Discard();
                return;
            }
        }

        // 2) Unknown ratio via compute (only if motion did not trigger)
        //    Dispatch asynchronously (no CPU wait); consume last completed result if available.
        var tex = dieInput.FrameTex;
        if (tex != null){
            int w = tex.width;
            int h = tex.height;
            if (w <= 0 || h <= 0) throw new InvalidOperationException("DieByRule: invalid dieInput texture size");

            // Consume latest result (from previous frame's request)
            if (_hasLatestUnknown){
                float ratio = _latestUnknownRatio;
                _hasLatestUnknown = false;
                if (!_emaInit){
                    _emaUnknownRatio = ratio;
                    _emaInit = true;
                } else {
                    float a = Mathf.Clamp01(coverageSmooth);
                    _emaUnknownRatio = Mathf.Lerp(ratio, _emaUnknownRatio, 1f - a);
                }

                if (_emaUnknownRatio >= unknownRatioThresh){
                    if (logVerbose) Debug.Log($"{logPrefix} DIE by coverage: ratio={_emaUnknownRatio:F3}");
                    state.Discard();
                    return;
                }
            }

            // If no request is in flight, dispatch a new one now
            if (!_requestInFlight){
                _cpuCounter[0] = 0u; _cpuCounter[1] = 0u;
                _counter.SetData(_cpuCounter);

                holeCountCS.SetTexture(_kernel, _propTex, tex);
                holeCountCS.SetInt(_propW, w);
                holeCountCS.SetInt(_propH, h);
                holeCountCS.SetBuffer(_kernel, _propOut, _counter);

                int gx = (w + 15) / 16;
                int gy = (h + 15) / 16;
                holeCountCS.Dispatch(_kernel, gx, gy, 1);

                _lastDispatchW = w;
                _lastDispatchH = h;
                _requestGeneration = poseDiff.Generation;
                _requestInFlight = true;
                AsyncGPUReadback.Request(_counter, OnReadbackComplete);
            }
        }
    }

    private void OnReadbackComplete(AsyncGPUReadbackRequest req){
        if (req.hasError){
            _requestInFlight = false;
            throw new InvalidOperationException("DieByRule: AsyncGPUReadback failed");
        }
        // Ignore outdated results (different life generation)
        if (poseDiff == null || _requestGeneration != poseDiff.Generation){
            _requestInFlight = false;
            return;
        }
        var data = req.GetData<uint>();
        if (data.Length < 1){
            _requestInFlight = false;
            throw new InvalidOperationException("DieByRule: counter length invalid");
        }
        uint invalid = data[0];
        uint total = (uint)(_lastDispatchW * _lastDispatchH);
        float ratio = total > 0 ? (float)invalid / (float)total : 0f;
        _latestUnknownRatio = ratio;
        _hasLatestUnknown = true;
        _requestInFlight = false;
    }
}


