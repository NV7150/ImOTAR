using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Sentis;
using System.Collections.Generic;

/// <summary>
/// Sentis 2.1.x: Wrapper for iterative inference via Worker.ScheduleIterable.
/// - Begin: preprocess (letterbox + ToTensor) once via CommandBuffer → ScheduleIterable(feeds)
/// - Step(n): call MoveNext() n times (no external CommandBuffer needed)
/// - On finalization: write model output to textures (RenderToTexture → ROI copy)
/// </summary>
public class PDAItrProcessor : DepthModelIterableProcessor {
    [Header("Output")]
    [SerializeField] private RenderTexture outputRT;          // Final output (RFloat, newW x newH)
    [Header("Model Configuration")]
    [SerializeField] private ModelAsset promptDaOnnx;

    [Header("ONNX Settings")]
    [SerializeField] private int onnxWidth = 3836;
    [SerializeField] private int onnxHeight = 2156;

    [Header("Processing Settings")]
    [SerializeField] private BackendType backendType = BackendType.GPUCompute;
    
    [Header("Completion")]
    [SerializeField, Min(0)] private int completionFrameDelay = 1; // Completion promotion delay (frames)

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[PromptDA-ITER]";

    [Header("Input Sources")]
    [SerializeField] private FrameProvider cameraRec;
    [SerializeField] private FrameProvider depthRec;
    [Header("Sync Settings")]
    [SerializeField] private float maxTimeSyncDifferenceMs = 100f;

    // Constants
    private const int LETTERBOX_MULTIPLE = 14;
    private const int RGB_CHANNELS = 3;
    private const int DEPTH_CHANNELS = 1;

    // Runtime
    private Model  _runtimeModel;
    private Worker _worker;

    // GPU resources
    private RenderTexture _resizeRGB, _canvasRGB;     // ARGB32
    private RenderTexture _resizeDepth, _canvasDepth; // RFloat
    private Tensor<float> _imgTensorGPU;              // [1,3,H,W]
    private Tensor<float> _promptTensorGPU;           // [1,1,H,W]
    private RenderTexture _fullOutputRT;              // dstW x dstH, RFloat

    // Letterbox cache
    private int _dstW, _dstH, _newW, _newH, _padX, _padY;
    private bool _letterboxParamsValid = false;

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
    private int _finalizeFrame = -1;    // Finalize を発行したフレーム番号（finalizedJobId用）
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
    private bool _consumedLatest = true; // consume gate

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

        ReleaseRT(ref _resizeRGB);   
        ReleaseRT(ref _canvasRGB);
        ReleaseRT(ref _resizeDepth); 
        ReleaseRT(ref _canvasDepth);
        ReleaseRT(ref _fullOutputRT);

        _imgTensorGPU?.Dispose();    _imgTensorGPU = null;
        _promptTensorGPU?.Dispose(); _promptTensorGPU = null;
    }

    /// <summary> Start a new inference (preprocess + initialize iterator). Does not fully execute yet. </summary>
    private bool Begin(RenderTexture rgbInput, RenderTexture depthInput, DateTime timestamp) {
        if (!IsInitialized || rgbInput == null || depthInput == null) {
            if (verboseLogging)
                Debug.LogWarning($"{logPrefix} Begin: invalid (IsInit={IsInitialized}, rgbNull={rgbInput==null}, depNull={depthInput==null})");
            return false;
        }
        if (_running) {
            if (verboseLogging)
                Debug.LogWarning($"{logPrefix} Begin: rejected (already running)");
            return false; // 単一ジョブ前提
        }

        // Allocate resources and compute letterbox parameters on first run
        if (!_letterboxParamsValid) {
            ComputeLetterboxParams(rgbInput.width, rgbInput.height,
                                   onnxWidth, onnxHeight, LETTERBOX_MULTIPLE,
                                   out _newW, out _newH, out _padX, out _padY);
            _dstW = onnxWidth; _dstH = onnxHeight;
            _letterboxParamsValid = true;
            EnsureResources();
            ResolveInputIndices();

            if (verboseLogging) {
                Debug.Log($"{logPrefix} Letterbox: src=({rgbInput.width}x{rgbInput.height}), dst=({_dstW}x{_dstH}), new=({_newW}x{_newH}), pad=({_padX},{_padY})");
                Debug.Log($"{logPrefix} RTs: canvasRGB={_canvasRGB.descriptor}, canvasDepth={_canvasDepth.descriptor}, out={(outputRT!=null ? outputRT.descriptor.ToString() : "null")}");
                Debug.Log($"{logPrefix} Inputs: imageIdx={_imageInputIndex}({_imageInputName}), promptIdx={_promptInputIndex}({_promptInputName})");
            }
        }

        // Preprocess (letterbox + ToTensor) once via CommandBuffer
        using (var cb = new CommandBuffer { name = "PromptDA Iterable Preprocess" }) {
            // RGB
            cb.Blit(rgbInput, _resizeRGB);
            cb.CopyTexture(_resizeRGB,  0,0, 0,0, _newW,_newH, _canvasRGB,  0,0, _padX,_padY);
            // Depth
            cb.Blit(depthInput, _resizeDepth);
            cb.CopyTexture(_resizeDepth,0,0, 0,0, _newW,_newH, _canvasDepth,0,0, _padX,_padY);
            // ToTensor (NCHW)
            var tfRGB = new TextureTransform().SetDimensions(_dstW, _dstH, RGB_CHANNELS).SetTensorLayout(TensorLayout.NCHW);
            var tfDEP = new TextureTransform().SetDimensions(_dstW, _dstH, DEPTH_CHANNELS).SetTensorLayout(TensorLayout.NCHW);
            cb.ToTensor(_canvasRGB,  _imgTensorGPU,    tfRGB);
            cb.ToTensor(_canvasDepth,_promptTensorGPU, tfDEP);
            Graphics.ExecuteCommandBuffer(cb);
        }

        // Initialize iterator via worker.ScheduleIterable
        var feeds = new Tensor[2];
        feeds[_imageInputIndex]  = _imgTensorGPU;
        feeds[_promptInputIndex] = _promptTensorGPU;
        _iter = _worker.ScheduleIterable(feeds);

        _running       = true;
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
                    using (var cb = new CommandBuffer { name = "PromptDA Iterable Finalize" }) {
                        cb.RenderToTexture(outRef, _fullOutputRT);
                        if (outputRT != null) {
                            cb.CopyTexture(_fullOutputRT, 0,0, _padX,_padY, _newW,_newH, outputRT, 0,0, 0,0);
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

    // ---- Diagnostics -------------------------------------------------------

    // ---- 内部ヘルパ -------------------------------------------------------

    private void InitializeModelAndWorker() {
        if (promptDaOnnx == null) {
            Debug.LogError($"{logPrefix} Model is not assigned!");
            return;
        }
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
        ReleaseRT(ref _resizeRGB);   ReleaseRT(ref _canvasRGB);
        ReleaseRT(ref _resizeDepth); ReleaseRT(ref _canvasDepth);
        ReleaseRT(ref _fullOutputRT);
        _imgTensorGPU?.Dispose();    _imgTensorGPU = null;
        _promptTensorGPU?.Dispose(); _promptTensorGPU = null;

        _resizeRGB   = AllocRT(_newW, _newH, RenderTextureFormat.ARGB32, false);
        _canvasRGB   = AllocRT(_dstW, _dstH, RenderTextureFormat.ARGB32, false);
        _resizeDepth = AllocRT(_newW, _newH, RenderTextureFormat.RFloat,  false);
        _canvasDepth = AllocRT(_dstW, _dstH, RenderTextureFormat.RFloat,  false);

        _fullOutputRT = AllocRT(_dstW, _dstH, RenderTextureFormat.RFloat, true);

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

    private static void ComputeLetterboxParams(int srcW, int srcH, int dstW, int dstH, int multiple, out int newW, out int newH, out int padX, out int padY) {
        float scale = Mathf.Min((float)dstW / srcW, (float)dstH / srcH);
        newW = Mathf.Max(multiple, Mathf.FloorToInt(srcW * scale));
        newH = Mathf.Max(multiple, Mathf.FloorToInt(srcH * scale));
        newW -= newW % multiple;
        newH -= newH % multiple;
        padX = (dstW - newW) / 2;
        padY = (dstH - newH) / 2;
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
