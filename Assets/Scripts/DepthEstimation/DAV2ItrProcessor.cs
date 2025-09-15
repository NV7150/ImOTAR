using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Sentis;

/// <summary>
/// Depth Anything v2 (fixed 728x546) iterable processor using Sentis 2.x Worker.ScheduleIterable.
/// - Preprocess once: letterbox into fixed canvas, then ToTensor (RGB [0,1], NCHW)
/// - Iterate: MoveNext steps until done
/// - Finalize: RenderToTexture and ROI copy to output
/// No fallbacks: any missing configuration or invalid state throws exceptions.
/// </summary>
public class DAV2ItrProcessor : DepthModelIterableProcessor {
    [Header("Output")]
    [SerializeField] private RenderTexture outputRT;

    [Header("Model Configuration")]
    [SerializeField] private ModelAsset onnxModel;

    [Header("ONNX Settings (Fixed)")]
    [SerializeField, Min(1)] private int onnxWidth  = 728;   // must match model
    [SerializeField, Min(1)] private int onnxHeight = 546;   // must match model
    [SerializeField] private string imageInputName  = "image"; // default; resolved from model if different
    [SerializeField] private string depthOutputName = "depth"; // default; resolved from model if different

    [Header("Processing Settings")]
    [SerializeField] private BackendType backendType = BackendType.GPUCompute;
    [SerializeField, Min(1)] private int letterboxMultiple = 14;

    [Header("Completion")]
    [SerializeField, Min(0)] private int completionFrameDelay = 1;
    [Header("Output Normalization (Relative model)")]
    [SerializeField, Tooltip("If true, normalize model output per-frame to [0,1] via min-max.")]
    private bool normalizeRelativeOutput = false;
    [SerializeField, Min(0f), Tooltip("Small epsilon to avoid division by zero during normalization.")]
    private float normalizationEpsilon = 1e-6f;
    [SerializeField, Min(0f), Tooltip("Positive threshold for reference depth mask (M > 0). Values >= tau are treated as valid.")]
    private float refDepthPositiveTau = 1e-3f;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[DAV2-ITER]";

    [Header("Input Sources")]
    [SerializeField] private FrameProvider cameraRec;
    [SerializeField, Tooltip("Max ms difference allowed when synchronizing RGB only (kept for parity; not used)")]
    private float maxTimeSyncDifferenceMs = 100f;

    [Header("Calibration")]
    [SerializeField] private bool useCalibration = false;
    [SerializeField] private FrameProvider referenceDepthRec;
    [SerializeField, Min(1)] private int minValidPixels = 1024;
    [SerializeField] private float maxTimeSyncDifferenceMsRef = 50f;
    public enum CalibrationMethod { L2, HuberIRLS }
    [SerializeField] private CalibrationMethod calibrationMethod = CalibrationMethod.L2;
    [SerializeField, Min(1)] private int irlsIterations = 2;
    [SerializeField, Min(0f)] private float huberDelta = 0.1f;

    // Constants
    private const int RGB_CHANNELS = 3;
    private const int DEPTH_CHANNELS = 1;

    // Runtime
    private Model _runtimeModel;
    private Worker _worker;
    private int _baseInputCount;

    // GPU resources
    private RenderTexture _resizeRGB, _canvasRGB;   // ARGB32
    private Tensor<float> _imgTensorGPU;            // [1,3,H,W]
    private RenderTexture _fullOutputRT;            // [dstW x dstH] RFloat
    private RenderTexture _resizeRefDepth, _refDepthCanvas; // RFloat
    private Tensor<float> _refTensorGPU;                    // [1,1,H,W]

    // Letterbox cache
    private int _dstW, _dstH, _newW, _newH, _padX, _padY;
    private bool _letterboxParamsValid = false;

    // Model IO indices
    private int _imageInputIndex = 0;
    private int _refInputIndex = -1;

    // Execution state
    private IEnumerator _iter;
    private bool _running = false;
    private bool _supportsAsyncCompute;

    // Job tracking
    private Guid _currentJobId = Guid.Empty;
    private Guid _finalizedJobId = Guid.Empty;
    private Guid _completedJobId = Guid.Empty;
    private int _finalizeFrame = -1;
    private HashSet<Guid> _invalidJobIds = new HashSet<Guid>();

    // Input cache
    private struct FrameData {
        public DateTime timestamp;
        public RenderTexture rgbFrame;
        public bool isValid;
    }
    private FrameData _latestRgb;
    private FrameData _latestRef;
    private bool _consumedLatest = true;

    // Meta (abstract props)
    private bool _isInitialized;
    private bool _ownsOutputRT = false;
    public override bool IsInitialized => _isInitialized;
    public override RenderTexture ResultRT => outputRT;
    private DateTime _currentTimestamp;
    public override DateTime CurrentTimestamp => _currentTimestamp;
    public override bool IsRunning => _running;
    public override Guid CurrentJobId   => _currentJobId;
    public override Guid FinalizedJobId => _finalizedJobId;
    public override Guid CompletedJobId => _completedJobId;

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
        _worker?.Dispose(); _worker = null;

        ReleaseRT(ref _resizeRGB);
        ReleaseRT(ref _canvasRGB);
        ReleaseRT(ref _fullOutputRT);
        ReleaseRT(ref _resizeRefDepth);
        ReleaseRT(ref _refDepthCanvas);

        _imgTensorGPU?.Dispose(); _imgTensorGPU = null;
        _refTensorGPU?.Dispose(); _refTensorGPU = null;

        if (_ownsOutputRT) {
            ReleaseRT(ref outputRT);
            _ownsOutputRT = false;
        }
    }

    // ---- Public API (DepthModelIterableProcessor) ----
    public override void SetupInputSubscriptions(){
        if (cameraRec == null)
            throw new InvalidOperationException($"{logPrefix} cameraRec is not assigned.");
        cameraRec.OnFrameUpdated -= OnRgbFrameReceived;
        cameraRec.OnFrameUpdated += OnRgbFrameReceived;
        if (useCalibration) {
            if (referenceDepthRec == null)
                throw new InvalidOperationException($"{logPrefix} referenceDepthRec is not assigned while useCalibration=true.");
            referenceDepthRec.OnFrameUpdated -= OnRefDepthReceived;
            referenceDepthRec.OnFrameUpdated += OnRefDepthReceived;
        }
    }

    public override Guid TryStartProcessing(){
        if (!IsInitialized)
            throw new InvalidOperationException($"{logPrefix} Not initialized.");
        if (outputRT == null)
            throw new InvalidOperationException($"{logPrefix} outputRT is null.");
        if (_running)
            return Guid.Empty;
        if (!_latestRgb.isValid)
            return Guid.Empty;
        if (useCalibration) {
            if (!_latestRef.isValid)
                return Guid.Empty;
            var dtMs = Mathf.Abs((float)(_latestRgb.timestamp - _latestRef.timestamp).TotalMilliseconds);
            if (dtMs > maxTimeSyncDifferenceMsRef)
                throw new InvalidOperationException($"{logPrefix} Ref depth not synchronized: dtMs={dtMs} > {maxTimeSyncDifferenceMsRef}");
        }

        var ts = _latestRgb.timestamp;
        var ok = Begin(_latestRgb.rgbFrame, ts);
        if (!ok)
            throw new InvalidOperationException($"{logPrefix} Begin returned false unexpectedly.");

        _currentJobId = Guid.NewGuid();
        if (verboseLogging)
            Debug.Log($"{logPrefix} Begin OK: jobId={_currentJobId}, ts={ts:HH:mm:ss.fff}");
        _consumedLatest = true;
        _latestRgb.isValid = false;
        if (useCalibration) _latestRef.isValid = false;
        return _currentJobId;
    }

    public override void Step(int steps){
        if (steps <= 0) return;
        if (!_running || _iter == null) return;

        for (int k = 0; k < steps; k++) {
            bool hasMore = false;
            try {
                hasMore = _iter.MoveNext();
            } catch (Exception e) {
                _running = false;
                throw new InvalidOperationException($"{logPrefix} Step MoveNext exception: {e}");
            }

            if (!hasMore) {
                var outRef = _worker.PeekOutput() as Tensor<float>;
                if (outRef == null)
                    throw new InvalidOperationException($"{logPrefix} Output tensor is null or wrong type.");

                bool isInvalid = (_currentJobId != Guid.Empty) && _invalidJobIds.Contains(_currentJobId);
                if (!isInvalid) {
                    using (var cb = new CommandBuffer { name = "DAV2 Iterable Finalize" }) {
                        cb.RenderToTexture(outRef, _fullOutputRT);
                        if (outputRT == null)
                            throw new InvalidOperationException($"{logPrefix} outputRT became null during finalize.");
                        cb.CopyTexture(_fullOutputRT, 0,0, _padX,_padY, _newW,_newH, outputRT, 0,0, 0,0);
                        Graphics.ExecuteCommandBuffer(cb);
                    }
                }

                _running = false;
                _finalizedJobId = _currentJobId;
                _finalizeFrame = Time.frameCount;
                if (verboseLogging) {
                    var p = HasDelayElapsed();
                    Debug.Log($"{logPrefix} Finalize: frame={Time.frameCount}, finalized={(_finalizedJobId!=Guid.Empty)}, passed={p}, applied={!isInvalid}");
                }
                break;
            }
        }

        if (_finalizedJobId != Guid.Empty && _completedJobId != _finalizedJobId && HasDelayElapsed()) {
            _completedJobId = _finalizedJobId;
            _invalidJobIds.Remove(_completedJobId);
            if (verboseLogging)
                Debug.Log($"{logPrefix} Complete: jobId={_completedJobId}");
        }
    }

    public override void InvalidateJob(Guid jobId){
        if (jobId == Guid.Empty) return;
        _invalidJobIds.Add(jobId);
        if (verboseLogging)
            Debug.Log($"{logPrefix} InvalidateJob: {jobId}");
    }

    public override void FillOutput(float value){
        var rt = outputRT;
        if (rt == null) throw new InvalidOperationException($"{logPrefix} outputRT is null in FillOutput.");
        var active = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, new Color(value, value, value, 1f));
        RenderTexture.active = active;
    }

    public override void ClearOutput(){
        FillOutput(0f);
    }

    // ---- Internal helpers ----
    private void InitializeModelAndWorker(){
        if (onnxModel == null)
            throw new InvalidOperationException($"{logPrefix} Model is not assigned.");
        if (onnxWidth <= 0 || onnxHeight <= 0)
            throw new InvalidOperationException($"{logPrefix} Invalid onnx size: {onnxWidth}x{onnxHeight}");
        if (letterboxMultiple <= 0)
            throw new InvalidOperationException($"{logPrefix} letterboxMultiple must be > 0");

        var baseModel = ModelLoader.Load(onnxModel);
        _baseInputCount = baseModel.inputs != null ? baseModel.inputs.Count : 0;
        ResolveNamesFromModel(baseModel);

        if (useCalibration) {
            if (!normalizeRelativeOutput)
                throw new InvalidOperationException($"{logPrefix} useCalibration requires normalizeRelativeOutput=true.");
            _runtimeModel = BuildNormalizedAndCalibratedModel(baseModel);
        } else if (normalizeRelativeOutput) {
            _runtimeModel = BuildNormalizedModel(baseModel);
        } else {
            _runtimeModel = baseModel;
        }
        _worker = new Worker(_runtimeModel, backendType);
        ResolveInputIndicesFromRuntimeModel(_runtimeModel);
        _isInitialized = true;
        if (verboseLogging)
            Debug.Log($"{logPrefix} Model loaded. inputs={_runtimeModel.inputs.Count}, outputs={_runtimeModel.outputs.Count}, backend={backendType}");
    }

    private Model BuildNormalizedModel(Model baseModel){
        // Validate expected shapes (fixed NCHW with 1x1xH x W output)
        var g = new FunctionalGraph();
        var fInputs = g.AddInputs(baseModel);

        // Forward through base model
        var fOuts = Functional.Forward(baseModel, fInputs);
        if (fOuts == null || fOuts.Length == 0)
            throw new InvalidOperationException($"{logPrefix} Base model produced no outputs.");
        var depth = fOuts[0]; // expect [1,1,H,W] already

        // Reduce over C,H,W axes (1,2,3) with keepdims=true to get broadcastable scalars
        var axes = new int[] { 1, 2, 3 };
        var dMin = Functional.ReduceMin(depth, axes, true);
        var dMax = Functional.ReduceMax(depth, axes, true);
        var denom = dMax - dMin + normalizationEpsilon;
        var norm  = Functional.Clamp((dMax - depth) / denom, 0f, 1f);

        return g.Compile(norm);
    }
private Model BuildNormalizedAndCalibratedModel(Model baseModel){
    var g = new FunctionalGraph();
    var fInputs = g.AddInputs(baseModel); // image only
    var refDepth = g.AddInput<float>(new DynamicTensorShape(1, DEPTH_CHANNELS, onnxHeight, onnxWidth));

    var fOuts = Functional.Forward(baseModel, fInputs);
    if (fOuts == null || fOuts.Length == 0)
        throw new InvalidOperationException($"{logPrefix} Base model produced no outputs.");
    var raw = fOuts[0]; // [1,1,H,W]

    // ---- フレーム内正規化（自然形）----
    var axes = new int[] { 1, 2, 3 };
    var rMin = Functional.ReduceMin(raw, axes, true);
    var rMax = Functional.ReduceMax(raw, axes, true);
    var denom = rMax - rMin + normalizationEpsilon;
    var z0 = Functional.Clamp((raw - rMin) / denom, 0f, 1f); // [0,1]

    // ---- 無効=-1 を除外する連続マスク（refDepth>tau のみ有効）----
    var tauC = Functional.Constant(refDepthPositiveTau);
    var diffValid = refDepth - tauC;
    var posValid  = Functional.Clamp(diffValid, 0f, 1e9f);                 // ReLU
    var maskValid = posValid / (posValid + normalizationEpsilon);          // 0..1

    // ---- 向きの自動決定（z0 と refDepth の加重共分散の符号）----
    var n0   = Functional.ReduceSum(maskValid, axes, false);
    var Sx0  = Functional.ReduceSum(z0 * maskValid, axes, false);
    var Sy0  = Functional.ReduceSum(refDepth * maskValid, axes, false);
    var Sxx0 = Functional.ReduceSum(z0 * z0 * maskValid, axes, false);
    var Sxy0 = Functional.ReduceSum(z0 * refDepth * maskValid, axes, false);

    var invN0 = 1.0f / (n0 + normalizationEpsilon);
    var mx0   = Sx0 * invN0;
    var my0   = Sy0 * invN0;
    var cov0  = Sxy0 * invN0 - mx0 * my0;
    var sCov  = cov0 / (Functional.Abs(cov0) + normalizationEpsilon);      // ~sign(cov0)

    var half = Functional.Constant(0.5f);
    var one  = Functional.Constant(1f);
    var z    = half * ( (one + sCov) * z0 + (one - sCov) * (one - z0) );   // 向きを安定化

    // ---- 端点アンカー（高速・連続・GPUのみ）----
    // 近側帯域 z <= zNearTh、遠側帯域 z >= zFarTh を抽出し、
    // それぞれの refDepth 平均を "疑似サンプル" として加える。
    // アンカー重みは観測画素数に比例（anchorScale）。
    var zNearTh = Functional.Constant(0.05f);
    var zFarTh  = Functional.Constant(0.95f);
    var anchorScale = Functional.Constant(0.10f); // 0.1 * (#帯域画素) を疑似サンプル重みとする

    // 近側マスク: zNearTh - z の ReLU を0..1へ
    var mNeStep = Functional.Clamp(zNearTh - z, 0f, 1e9f);
    var mNeBand = (mNeStep) / (mNeStep + normalizationEpsilon);
    var mNe     = mNeBand * maskValid;

    // 遠側マスク: z - zFarTh の ReLU を0..1へ
    var mFaStep = Functional.Clamp(z - zFarTh, 0f, 1e9f);
    var mFaBand = (mFaStep) / (mFaStep + normalizationEpsilon);
    var mFa     = mFaBand * maskValid;

    var nNe = Functional.ReduceSum(mNe, axes, false);                        // 近側サンプル数
    var nFa = Functional.ReduceSum(mFa, axes, false);                        // 遠側サンプル数

    var yNe = Functional.ReduceSum(refDepth * mNe, axes, false) / (nNe + normalizationEpsilon); // 近側平均
    var yFa = Functional.ReduceSum(refDepth * mFa, axes, false) / (nFa + normalizationEpsilon); // 遠側平均

    var wNe = anchorScale * nNe;     // 近側アンカー重み（疑似サンプル数）
    var wFa = anchorScale * nFa;     // 遠側アンカー重み

    // アンカーは (z=0, y=yNe), (z=1, y=yFa) の2点を追加したのと等価
    var n    = Functional.ReduceSum(maskValid, axes, false);
    var Sx   = Functional.ReduceSum(z * maskValid, axes, false);
    var Sy   = Functional.ReduceSum(refDepth * maskValid, axes, false);
    var Sxx  = Functional.ReduceSum(z * z * maskValid, axes, false);
    var Sxy  = Functional.ReduceSum(z * refDepth * maskValid, axes, false);

    var nA   = n   + wNe + wFa;
    var SxA  = Sx  + wFa;                  // z=1 アンカーのみ寄与
    var SyA  = Sy  + wNe * yNe + wFa * yFa;
    var SxxA = Sxx + wFa;                  // (1)^2
    var SxyA = Sxy + wFa * yFa;            // 1*yFa

    var D    = nA * SxxA - SxA * SxA;
    var Dsafe= D + normalizationEpsilon * (1f + 1f / (nA + 1f));
    var alpha0 = (nA * SxyA - SxA * SyA) / Dsafe;
    var beta0  = (SyA - alpha0 * SxA) / (nA + normalizationEpsilon);

    var alpha = alpha0;
    var beta  = beta0;

    // ---- IRLS（使用時はアンカーを各反復に再注入）----
    if (calibrationMethod == CalibrationMethod.HuberIRLS && irlsIterations > 0) {
        for (int i = 0; i < irlsIterations; i++) {
            var r   = alpha * z + beta - refDepth;
            var abd = Functional.Sqrt(r * r + normalizationEpsilon);
            var w   = Functional.Clamp(huberDelta / abd, 0f, 1f); // Huber重み

            var Sw   = Functional.ReduceSum(maskValid * w, axes, false);
            var Swx  = Functional.ReduceSum(maskValid * w * z, axes, false);
            var Swy  = Functional.ReduceSum(maskValid * w * refDepth, axes, false);
            var Swxx = Functional.ReduceSum(maskValid * w * z * z, axes, false);
            var Swxy = Functional.ReduceSum(maskValid * w * z * refDepth, axes, false);

            var SwA   = Sw   + wNe + wFa;
            var SwxA  = Swx  + wFa;
            var SwyA  = Swy  + wNe * yNe + wFa * yFa;
            var SwxxA = Swxx + wFa;
            var SwxyA = Swxy + wFa * yFa;

            var D2    = SwA * SwxxA - SwxA * SwxA;
            var D2safe= D2 + normalizationEpsilon * (1f + 1f / (SwA + 1f));
            var aNew  = (SwA * SwxyA - SwxA * SwyA) / D2safe;
            var bNew  = (SwyA - aNew * SwxA) / (SwA + normalizationEpsilon);

            alpha = aNew;
            beta  = bNew;
        }
    }

    var meters = Functional.Clamp(alpha * z + beta, 0f, 1e9f);
    return g.Compile(meters);
}




    private void ResolveNamesFromModel(Model model){
        // Input
        string resolvedInput = imageInputName;
        foreach (var inp in model.inputs){
            var n = inp.name ?? string.Empty;
            if (!string.IsNullOrEmpty(n) && n.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0) {
                resolvedInput = n; break;
            }
        }
        imageInputName = string.IsNullOrEmpty(resolvedInput) ? throw new InvalidOperationException($"{logPrefix} Cannot resolve input name for image") : resolvedInput;
        // No output name reliance after composition
    }

    private void ResolveInputIndicesFromRuntimeModel(Model model){
        int count = model.inputs != null ? model.inputs.Count : 0;
        if (count <= 0)
            throw new InvalidOperationException($"{logPrefix} Runtime model has no inputs.");
        // Base model inputs come first; image is the first input for DAV2
        _imageInputIndex = 0;
        if (useCalibration) {
            if (count <= _baseInputCount)
                throw new InvalidOperationException($"{logPrefix} Expected an extra ref_depth input but none was found.");
            _refInputIndex = count - 1; // appended input
        } else {
            _refInputIndex = -1;
        }
    }

    private bool Begin(RenderTexture rgbInput, DateTime timestamp){
        if (!IsInitialized)
            throw new InvalidOperationException($"{logPrefix} Begin called before initialization.");
        if (rgbInput == null)
            throw new ArgumentNullException(nameof(rgbInput), $"{logPrefix} rgbInput is null.");

        if (!_letterboxParamsValid){
            ComputeLetterboxParams(rgbInput.width, rgbInput.height,
                                   onnxWidth, onnxHeight, letterboxMultiple,
                                   out _newW, out _newH, out _padX, out _padY);
            _dstW = onnxWidth; _dstH = onnxHeight;
            _letterboxParamsValid = true;
            EnsureResources();
            EnsureOutputRT(_newW, _newH);
            if (verboseLogging){
                Debug.Log($"{logPrefix} Letterbox: src=({rgbInput.width}x{rgbInput.height}), dst=({_dstW}x{_dstH}), new=({_newW}x{_newH}), pad=({_padX},{_padY})");
                Debug.Log($"{logPrefix} RTs: canvasRGB={_canvasRGB.descriptor}, out={(outputRT!=null ? outputRT.descriptor.ToString() : "null")}");
            }
        }

        // Preprocess (letterbox + ToTensor)
        using (var cb = new CommandBuffer { name = "DAV2 Iterable Preprocess" }){
            cb.Blit(rgbInput, _resizeRGB);
            cb.CopyTexture(_resizeRGB, 0,0, 0,0, _newW,_newH, _canvasRGB, 0,0, _padX,_padY);
            var tfRGB = new TextureTransform().SetDimensions(_dstW, _dstH, RGB_CHANNELS).SetTensorLayout(TensorLayout.NCHW);
            cb.ToTensor(_canvasRGB, _imgTensorGPU, tfRGB);
            if (useCalibration){
                if (_latestRef.rgbFrame == null)
                    throw new InvalidOperationException($"{logPrefix} reference depth frame is null.");
                // Aspect ratio check (depth smaller but same ratio). Compute scale and verify
                float arRGB = (float)rgbInput.width / rgbInput.height;
                float arRef = (float)_latestRef.rgbFrame.width / _latestRef.rgbFrame.height;
                if (Mathf.Abs(arRGB - arRef) > 1e-3f)
                    throw new InvalidOperationException($"{logPrefix} Reference depth aspect ratio mismatch: rgb={arRGB}, ref={arRef}");
                // Upscale depth to RGB size (bilinear), then letterbox into canvas
                cb.Blit(_latestRef.rgbFrame, _resizeRefDepth);
                cb.CopyTexture(_resizeRefDepth, 0,0, 0,0, _newW,_newH, _refDepthCanvas, 0,0, _padX,_padY);
                var tfDEP = new TextureTransform().SetDimensions(_dstW, _dstH, 1).SetTensorLayout(TensorLayout.NCHW);
                cb.ToTensor(_refDepthCanvas, _refTensorGPU, tfDEP);
            }
            Graphics.ExecuteCommandBuffer(cb);
        }

        // Schedule iterable
        var feeds = new Tensor[_runtimeModel.inputs.Count];
        feeds[_imageInputIndex] = _imgTensorGPU;
        if (useCalibration) feeds[_refInputIndex] = _refTensorGPU;
        _iter = _worker.ScheduleIterable(feeds);

        _running = true;
        _currentTimestamp = timestamp;
        if (verboseLogging)
            Debug.Log($"{logPrefix} Begin: iterable started (frame={Time.frameCount}, t={Time.unscaledTime:0.000})");
        return true;
    }

    private void EnsureResources(){
        ReleaseRT(ref _resizeRGB); ReleaseRT(ref _canvasRGB); ReleaseRT(ref _fullOutputRT);
        _imgTensorGPU?.Dispose(); _imgTensorGPU = null;
        if (useCalibration) {
            ReleaseRT(ref _resizeRefDepth);
            ReleaseRT(ref _refDepthCanvas);
            _refTensorGPU?.Dispose(); _refTensorGPU = null;
        }

        _resizeRGB = AllocRT(_newW, _newH, RenderTextureFormat.ARGB32, false);
        _canvasRGB = AllocRT(_dstW, _dstH, RenderTextureFormat.ARGB32, false);
        _fullOutputRT = AllocRT(_dstW, _dstH, RenderTextureFormat.RFloat, true);

        _imgTensorGPU = new Tensor<float>(new TensorShape(1, RGB_CHANNELS, _dstH, _dstW));
        if (useCalibration) {
            _resizeRefDepth = AllocRT(_newW, _newH, RenderTextureFormat.RFloat, false);
            _refDepthCanvas = AllocRT(_dstW, _dstH, RenderTextureFormat.RFloat, false);
            _refTensorGPU = new Tensor<float>(new TensorShape(1, DEPTH_CHANNELS, _dstH, _dstW));
        }

        if (verboseLogging)
            Debug.Log($"{logPrefix} EnsureResources: allocated RT/Tensor (outRT={(outputRT!=null)})");
    }

    private static RenderTexture AllocRT(int w, int h, RenderTextureFormat fmt, bool enableRW){
        var rt = new RenderTexture(w, h, 0, fmt){
            enableRandomWrite = enableRW,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create();
        return rt;
    }

    private static void ReleaseRT(ref RenderTexture rt){
        if (rt == null) return;
        if (rt.IsCreated()) rt.Release();
        UnityEngine.Object.Destroy(rt);
        rt = null;
    }

    private void EnsureOutputRT(int w, int h){
        bool matches = outputRT != null && outputRT.width == w && outputRT.height == h && outputRT.format == RenderTextureFormat.RFloat;
        if (!matches) {
            if (_ownsOutputRT && outputRT != null) {
                ReleaseRT(ref outputRT);
            }
            // Never destroy assets we don't own; just replace the reference with an internally owned RT
            outputRT = AllocRT(w, h, RenderTextureFormat.RFloat, false);
            _ownsOutputRT = true;
            return;
        }
        if (!outputRT.IsCreated()) {
            outputRT.Create();
        }
    }

    private static void ComputeLetterboxParams(int srcW, int srcH, int dstW, int dstH, int multiple, out int newW, out int newH, out int padX, out int padY){
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0 || multiple <= 0)
            throw new ArgumentException("Invalid sizes for letterbox computation.");
        float scale = Mathf.Min((float)dstW / srcW, (float)dstH / srcH);
        newW = Mathf.Max(multiple, Mathf.FloorToInt(srcW * scale));
        newH = Mathf.Max(multiple, Mathf.FloorToInt(srcH * scale));
        newW -= newW % multiple;
        newH -= newH % multiple;
        padX = (dstW - newW) / 2;
        padY = (dstH - newH) / 2;
        if (newW <= 0 || newH <= 0 || padX < 0 || padY < 0)
            throw new InvalidOperationException("Letterbox computation resulted in invalid values.");
    }

    private bool HasDelayElapsed(){
        if (_finalizedJobId == Guid.Empty || _finalizeFrame < 0)
            return false;
        int wait = Mathf.Max(0, completionFrameDelay);
        return (Time.frameCount - _finalizeFrame) >= wait;
    }

    private void OnRgbFrameReceived(RenderTexture rgb){
        if (cameraRec == null)
            throw new InvalidOperationException($"{logPrefix} cameraRec is null in OnRgbFrameReceived.");
        _latestRgb = new FrameData{
            timestamp = cameraRec.TimeStamp,
            rgbFrame = rgb,
            isValid = true
        };
        _consumedLatest = false;
    }

    private void OnRefDepthReceived(RenderTexture depth){
        if (referenceDepthRec == null)
            throw new InvalidOperationException($"{logPrefix} referenceDepthRec is null in OnRefDepthReceived.");
        _latestRef = new FrameData{
            timestamp = referenceDepthRec.TimeStamp,
            rgbFrame = depth,
            isValid = true
        };
        _consumedLatest = false;
    }
}


