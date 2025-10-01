using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Sentis;
using System.Collections.Generic;

/// <summary>
/// Sentis 2.1.x: Iterable inference without letterboxing. Supports optional H<->W swap for model inputs.
/// - Begin: simple resize (no padding) + ToTensor via CommandBuffer → ScheduleIterable(feeds)
/// - Step(n): call MoveNext() n times
/// - Finalize: RenderToTexture to model-size RT → copy/blit to outputRT (aspect must match, otherwise throws)
/// </summary>
public class PromptDAItrProcessor : DepthModelIterableProcessor {
    [Header("Output")]
    [SerializeField] private RenderTexture outputRT;          // Final output (RFloat)

    [Header("Model Configuration")]
    [SerializeField] private ModelAsset promptDaOnnx;

    [Header("ONNX Settings")]
    [SerializeField] private int onnxWidth = 3836;
    [SerializeField] private int onnxHeight = 2156;
    [SerializeField] private bool reverseInputWH = false;      // Swap input spatial dims: (H,W) <-> (W,H) for both inputs

    [Header("Processing Settings")]
    [SerializeField] private BackendType backendType = BackendType.GPUCompute;

    [Header("Completion")]
    [SerializeField, Min(0)] private int completionFrameDelay = 1; // Completion promotion delay (frames)

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[PromptDA-ITER(NLB)]";

    [Header("Input Sources")]
    [SerializeField] private FrameProvider cameraRec;
    [SerializeField] private FrameProvider depthRec;
    [Header("Sync Settings")]
    [SerializeField] private float maxTimeSyncDifferenceMs = 100f;

    // Constants
    private const int RGB_CHANNELS = 3;
    private const int DEPTH_CHANNELS = 1;

    // Runtime
    private Model  _runtimeModel;
    private Worker _worker;

    // GPU resources (no letterbox)
    private RenderTexture _procRGB;     // ARGB32, inW x inH
    private RenderTexture _procDepth;   // RFloat,  inW x inH
    private Tensor<float> _imgTensorGPU;      // [1,3,inH,inW]
    private Tensor<float> _promptTensorGPU;   // [1,1,inH,inW]
    private RenderTexture _fullOutputRT;      // outW x outH, RFloat

    // Cached dims
    private int _dstW, _dstH; // input dims (may be swapped)
    private int _outW, _outH; // output dims (fixed)
    private bool _resourcesReady = false;

    // Model input names and indices
    private string _imageInputName = "image";
    private string _promptInputName = "prompt_depth";
    private int _imageInputIndex = 0;
    private int _promptInputIndex = 1;

    // Execution state
    private IEnumerator _iter;          // IEnumerator returned by ScheduleIterable
    private bool _running = false;
    // Job tracking
    private Guid _currentJobId = Guid.Empty;
    private Guid _finalizedJobId = Guid.Empty;
    private Guid _completedJobId = Guid.Empty;
    private int _finalizeFrame = -1;    // Finalize frame index
    private bool _supportsAsyncCompute;
    private HashSet<Guid> _invalidJobIds = new HashSet<Guid>();

    // Input cache
    private struct FrameData {
        public DateTime timestamp;
        public RenderTexture rgbFrame;
        public RenderTexture depthFrame;
        public DateTime rgbTimestamp;
        public DateTime depthTimestamp;
        public bool isValid;
    }
    private FrameData _latestRgb;
    private FrameData _latestDepth;
    private bool _consumedLatest = true;

    // Meta (implement abstract props)
    private bool _isInitialized;
    public override bool IsInitialized => _isInitialized;
    public override Guid CurrentJobId   => _currentJobId;
    public override Guid FinalizedJobId => _finalizedJobId;
    public override Guid CompletedJobId => _completedJobId;
    private DateTime _currentTimestamp;
    public override DateTime CurrentTimestamp => _currentTimestamp;
    public override RenderTexture ResultRT => outputRT;
    public override bool IsRunning => _running;

    void Awake() {
        _supportsAsyncCompute = SystemInfo.supportsAsyncCompute;
        InitializeModelAndWorker();

        if (verboseLogging)
            Debug.Log($"{logPrefix} Awake: backend={backendType}, async={_supportsAsyncCompute}, platform={Application.platform}");
    }

    void OnDestroy() {
        if (verboseLogging)
            Debug.Log($"{logPrefix} OnDestroy: releasing resources");

        _iter = null;
        _worker?.Dispose();   _worker = null;

        ReleaseRT(ref _procRGB);
        ReleaseRT(ref _procDepth);
        ReleaseRT(ref _fullOutputRT);

        _imgTensorGPU?.Dispose();    _imgTensorGPU = null;
        _promptTensorGPU?.Dispose(); _promptTensorGPU = null;
    }

    /// <summary> Start a new inference (preprocess + initialize iterator). </summary>
    private bool Begin(RenderTexture rgbInput, RenderTexture depthInput, DateTime timestamp) {
        if (!IsInitialized)
            throw new InvalidOperationException($"{logPrefix} Begin: processor is not initialized");
        if (rgbInput == null)
            throw new ArgumentNullException(nameof(rgbInput), $"{logPrefix} Begin: rgbInput is null");
        if (depthInput == null)
            throw new ArgumentNullException(nameof(depthInput), $"{logPrefix} Begin: depthInput is null");
        if (outputRT == null)
            throw new InvalidOperationException($"{logPrefix} Begin: outputRT is not assigned");
        if (_running)
            return false; // single job enforced

        // Compute input (possibly swapped) and output (fixed) dims
        var inW = reverseInputWH ? onnxHeight : onnxWidth;
        var inH = reverseInputWH ? onnxWidth  : onnxHeight;
        var outW = onnxWidth;
        var outH = onnxHeight;
        if (inW <= 0 || inH <= 0 || outW <= 0 || outH <= 0)
            throw new ArgumentException($"{logPrefix} Begin: invalid dims in=({inW}x{inH}), out=({outW}x{outH})");

        // Aspect check for output only
        if ((long)outW * outputRT.height != (long)outH * outputRT.width)
            throw new InvalidOperationException($"{logPrefix} Aspect mismatch: out=({outW}x{outH}), outputRT=({outputRT.width}x{outputRT.height})");

        // Recreate resources if needed
        if (!_resourcesReady || _dstW != inW || _dstH != inH || _outW != outW || _outH != outH) {
            _dstW = inW;
            _dstH = inH;
            _outW = outW;
            _outH = outH;
            EnsureResources();
            ResolveInputIndices();
            _resourcesReady = true;

            if (verboseLogging) {
                Debug.Log($"{logPrefix} Init: in=({_dstW}x{_dstH}), out=({_outW}x{_outH}), reverseInputWH={reverseInputWH}, outputRT=({outputRT.width}x{outputRT.height})");
                Debug.Log($"{logPrefix} RTs: procRGB={_procRGB.descriptor}, procDepth={_procDepth.descriptor}, outRT={_fullOutputRT.descriptor}");
                Debug.Log($"{logPrefix} Inputs: imageIdx={_imageInputIndex}({_imageInputName}), promptIdx={_promptInputIndex}({_promptInputName})");
            }
        }

        // Preprocess (resize only) + ToTensor
        using (var cb = new CommandBuffer { name = "PromptDA Iterable Preprocess (NoLetterbox)" }) {
            cb.Blit(rgbInput, _procRGB);
            cb.Blit(depthInput, _procDepth);

            var tfRGB = new TextureTransform().SetDimensions(_dstW, _dstH, RGB_CHANNELS).SetTensorLayout(TensorLayout.NCHW);
            var tfDEP = new TextureTransform().SetDimensions(_dstW, _dstH, DEPTH_CHANNELS).SetTensorLayout(TensorLayout.NCHW);
            cb.ToTensor(_procRGB,  _imgTensorGPU,    tfRGB);
            cb.ToTensor(_procDepth,_promptTensorGPU, tfDEP);
            Graphics.ExecuteCommandBuffer(cb);
        }

        // Initialize iterator via worker.ScheduleIterable
        var feeds = new Tensor[2];
        feeds[_imageInputIndex]  = _imgTensorGPU;
        feeds[_promptInputIndex] = _promptTensorGPU;
        _iter = _worker.ScheduleIterable(feeds);

        _running = true;
        _currentTimestamp = timestamp;

        if (verboseLogging)
            Debug.Log($"{logPrefix} Begin: iterable started (frame={Time.frameCount}, t={Time.unscaledTime:0.000})");
        return true;
    }

    /// <summary> Advance the iterator by the specified number of steps. </summary>
    public override void Step(int steps) {
        if (!_running || _iter == null || steps <= 0) {
            return;
        }

        int advanced = 0;
        for (int k = 0; k < steps; k++) {
            bool hasMore = false;
            try {
                hasMore = _iter.MoveNext();
            } catch (Exception e) {
                Debug.LogError($"{logPrefix} Step MoveNext exception: {e}");
                hasMore = false;
            }
            advanced++;

            if (!hasMore) {
                // All layers scheduled; finalize and write outputs
                var outRef = _worker.PeekOutput() as Tensor<float>;
                bool isInvalid = (_currentJobId != Guid.Empty) && _invalidJobIds.Contains(_currentJobId);
                if (!isInvalid) {
                    using (var cb = new CommandBuffer { name = "PromptDA Iterable Finalize (NoLetterbox)" }) {
                        cb.RenderToTexture(outRef, _fullOutputRT);
                        if (outputRT != null) {
                            // If same size, CopyTexture; otherwise Blit (aspect checked at Begin)
                            if (outputRT.width == _outW && outputRT.height == _outH) {
                                cb.CopyTexture(_fullOutputRT, 0,0, 0,0, _outW,_outH, outputRT, 0,0, 0,0);
                            } else {
                                cb.Blit(_fullOutputRT, outputRT);
                            }
                        }
                        Graphics.ExecuteCommandBuffer(cb);
                    }
                }

                _running   = false;
                _finalizedJobId = _currentJobId;
                _finalizeFrame = Time.frameCount;
                if (verboseLogging) {
                    var p = HasDelayElapsed();
                    Debug.Log($"{logPrefix} Finalize: frame={Time.frameCount}, finalized={(_finalizedJobId!=Guid.Empty)}, passed={p}, applied={!isInvalid}");
                }
                break;
            }
        }

        // internal promotion (processor responsibility)
        if (_finalizedJobId != Guid.Empty && _completedJobId != _finalizedJobId && HasDelayElapsed()) {
            _completedJobId = _finalizedJobId;
            _invalidJobIds.Remove(_completedJobId);
            if (verboseLogging)
                Debug.Log($"{logPrefix} Complete: jobId={_completedJobId}");
        }
    }

    // ---- 内部ヘルパ -------------------------------------------------------

    private void InitializeModelAndWorker() {
        if (promptDaOnnx == null)
            throw new InvalidOperationException($"{logPrefix} Model is not assigned!");
        _runtimeModel = ModelLoader.Load(promptDaOnnx);
        ResolveInputNamesFromModel(_runtimeModel);
        _worker = new Worker(_runtimeModel, backendType);
        _isInitialized = true;
        if (verboseLogging)
            Debug.Log($"{logPrefix} Model loaded. inputs={_runtimeModel.inputs.Count}, backend={backendType}");
    }

    private void ResolveInputNamesFromModel(Model model) {
        foreach (var inp in model.inputs) {
            var n = inp.name ?? string.Empty;
            if (n.IndexOf("image",  StringComparison.OrdinalIgnoreCase) >= 0)
                _imageInputName  = n;
            if (n.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("depth",  StringComparison.OrdinalIgnoreCase) >= 0)
                _promptInputName = n;
        }
    }

    private void ResolveInputIndices() {
        var inputs = _runtimeModel.inputs;
        for (int i = 0; i < inputs.Count; i++) {
            var nm = inputs[i].name ?? string.Empty;
            if (nm.IndexOf(_imageInputName,  StringComparison.OrdinalIgnoreCase) >= 0)
                _imageInputIndex  = i;
            if (nm.IndexOf(_promptInputName, StringComparison.OrdinalIgnoreCase) >= 0)
                _promptInputIndex = i;
        }
        if (verboseLogging)
            Debug.Log($"{logPrefix} ResolveInputIndices: imageIndex={_imageInputIndex}, promptIndex={_promptInputIndex}");
    }

    private void EnsureResources() {
        ReleaseRT(ref _procRGB);
        ReleaseRT(ref _procDepth);
        ReleaseRT(ref _fullOutputRT);
        _imgTensorGPU?.Dispose();    _imgTensorGPU = null;
        _promptTensorGPU?.Dispose(); _promptTensorGPU = null;

        _procRGB     = AllocRT(_dstW, _dstH, RenderTextureFormat.ARGB32, false);
        _procDepth   = AllocRT(_dstW, _dstH, RenderTextureFormat.RFloat,  false);
        _fullOutputRT= AllocRT(_outW, _outH, RenderTextureFormat.RFloat,  true);

        _imgTensorGPU    = new Tensor<float>(new TensorShape(1, RGB_CHANNELS,  _dstH, _dstW));
        _promptTensorGPU = new Tensor<float>(new TensorShape(1, DEPTH_CHANNELS, _dstH, _dstW));

        if (verboseLogging)
            Debug.Log($"{logPrefix} EnsureResources: allocated RT/Tensor (outRT={(outputRT!=null)})");
    }

    private static RenderTexture AllocRT(int w, int h, RenderTextureFormat fmt, bool enableRW) {
        var rt = new RenderTexture(w, h, 0, fmt) {
            enableRandomWrite = enableRW,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create();
        return rt;
    }

    private static void ReleaseRT(ref RenderTexture rt) {
        if (rt == null) return;
        if (rt.IsCreated()) rt.Release();
        UnityEngine.Object.Destroy(rt);
        rt = null;
    }

    // Completion helper (N-frame delay)
    private bool HasDelayElapsed() {
        if (_finalizedJobId == Guid.Empty || _finalizeFrame < 0)
            return false;
        int wait = Mathf.Max(0, completionFrameDelay);
        return (Time.frameCount - _finalizeFrame) >= wait;
    }

    /// <summary>
    /// Disable final apply (write to outputRT) for the specified JobID.
    /// </summary>
    public override void InvalidateJob(Guid jobId) {
        if (jobId == Guid.Empty)
            return;
        _invalidJobIds.Add(jobId);
        if (verboseLogging)
            Debug.Log($"{logPrefix} InvalidateJob: {jobId}");
    }

    // ---- Abstract implementations for inputs/start/output ----
    public override void SetupInputSubscriptions(){
        if (cameraRec != null) {
            cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
            cameraRec.OnFrameUpdated += OnRgbFrameReceived;
        }
        if (depthRec != null) {
            depthRec.OnFrameUpdated -= OnDepthFrameReceived;
            depthRec.OnFrameUpdated += OnDepthFrameReceived;
        }
    }

    private void OnRgbFrameReceived(RenderTexture rgb){
        _latestRgb = new FrameData{
            timestamp = cameraRec != null ? cameraRec.TimeStamp : DateTime.MinValue,
            rgbFrame = rgb,
            rgbTimestamp = cameraRec != null ? cameraRec.TimeStamp : DateTime.MinValue,
            depthTimestamp = DateTime.MinValue,
            isValid = true
        };
        _consumedLatest = false;
    }

    private void OnDepthFrameReceived(RenderTexture depth){
        _latestDepth = new FrameData{
            timestamp = depthRec != null ? depthRec.TimeStamp : DateTime.MinValue,
            depthFrame = depth,
            rgbTimestamp = DateTime.MinValue,
            depthTimestamp = depthRec != null ? depthRec.TimeStamp : DateTime.MinValue,
            isValid = true
        };
        _consumedLatest = false;
    }

    public override Guid TryStartProcessing(){
        if (_running)
            return Guid.Empty;
        if (!IsInitialized || outputRT == null)
            return Guid.Empty;
        if (!_latestRgb.isValid || !_latestDepth.isValid)
            return Guid.Empty;
        var dtMs = Mathf.Abs((float)(_latestRgb.rgbTimestamp - _latestDepth.depthTimestamp).TotalMilliseconds);
        if (dtMs > maxTimeSyncDifferenceMs)
            return Guid.Empty;

        var ts = (_latestRgb.rgbTimestamp > _latestDepth.depthTimestamp) ? _latestRgb.rgbTimestamp : _latestDepth.depthTimestamp;
        var ok = Begin(_latestRgb.rgbFrame, _latestDepth.depthFrame, ts);
        if (!ok)
            return Guid.Empty;

        _currentJobId = Guid.NewGuid();
        if (verboseLogging)
            Debug.Log($"{logPrefix} Begin OK: jobId={_currentJobId}, ts={ts:HH:mm:ss.fff}");
        _consumedLatest = true;
        _latestRgb.isValid = false;
        _latestDepth.isValid = false;
        return _currentJobId;
    }

    public override void FillOutput(float value){
        var rt = outputRT;
        if (rt == null) return;
        var active = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, new Color(value, value, value, 1f));
        RenderTexture.active = active;
    }

    public override void ClearOutput(){
        FillOutput(0f);
    }
}


