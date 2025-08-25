using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Sentis;

/// <summary>
/// Sentis 2.1.x: Worker.ScheduleIterable を正しく使った分割実行ラッパ
/// - Begin: レタボ/ToTensor を CB で1回実行 → worker.ScheduleIterable(feeds)
/// - Step(n): n 回 MoveNext()（CBは不要）
/// - 完了時のみ CB を新規に作って RenderToTexture→ROI→Fence(CPUSynchronisation) を実行
/// </summary>
public class PromptDAIterableProcessor : MonoBehaviour
{
    [Header("Output")]
    [SerializeField] private RenderTexture outputRT;          // 最終出力先（RFloat, newW x newH）
    [Header("Model Configuration")]
    [SerializeField] private ModelAsset promptDaOnnx;

    [Header("ONNX Settings")]
    [SerializeField] private int onnxWidth = 3836;
    [SerializeField] private int onnxHeight = 2156;

    [Header("Processing Settings")]
    [SerializeField] private BackendType backendType = BackendType.GPUCompute;
    
    [Header("Completion")]
    [SerializeField, Min(0)] private int completionFrameDelay = 1; // N フレーム遅延で完了判定

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[PromptDA-ITER]";

    // 定数
    private const int LETTERBOX_MULTIPLE = 14;
    private const int RGB_CHANNELS = 3;
    private const int DEPTH_CHANNELS = 1;

    // ランタイム
    private Model  _runtimeModel;
    private Worker _worker;

    // 入出力GPU資源
    private RenderTexture _resizeRGB, _canvasRGB;     // ARGB32
    private RenderTexture _resizeDepth, _canvasDepth; // RFloat
    private Tensor<float> _imgTensorGPU;              // [1,3,H,W]
    private Tensor<float> _promptTensorGPU;           // [1,1,H,W]
    private RenderTexture _fullOutputRT;              // dstW x dstH, RFloat

    // レタボ計算キャッシュ
    private int _dstW, _dstH, _newW, _newH, _padX, _padY;
    private bool _letterboxParamsValid = false;

    // 入力名とインデックス
    private string _imageInputName = "image";
    private string _promptInputName = "prompt_depth";
    private int _imageInputIndex = 0;
    private int _promptInputIndex = 1;

    // 実行ステート
    private IEnumerator _iter;          // Worker.ScheduleIterable の IEnumerator
    private bool _running = false;      // MoveNext を残している
    // 世代（ジョブ）管理
    private System.Guid _currentJobId = System.Guid.Empty;
    private System.Guid _finalizedJobId = System.Guid.Empty;
    private System.Guid _completedJobId = System.Guid.Empty;
    private int _finalizeFrame = -1;    // Finalize を発行したフレーム番号（finalizedJobId用）
    private bool _supportsAsyncCompute;

    // メタ
    public bool IsInitialized { get; private set; }
    // 公開するのは世代IDのみ（boolではなく世代の状態で判定させる）
    public System.Guid CurrentJobId   => _currentJobId;
    public System.Guid FinalizedJobId => _finalizedJobId;
    public System.Guid CompletedJobId => _completedJobId;
    public DateTime CurrentTimestamp { get; private set; }
    public RenderTexture ResultRT => outputRT;

    void Awake()
    {
        _supportsAsyncCompute = SystemInfo.supportsAsyncCompute;
        InitializeModelAndWorker();

        if (verboseLogging)
            Debug.Log($"{logPrefix} Awake: backend={backendType}, async={_supportsAsyncCompute}, platform={Application.platform}");
    }

    void OnDestroy()
    {
        if (verboseLogging) Debug.Log($"{logPrefix} OnDestroy: releasing resources");

        _iter = null;
        _worker?.Dispose();   _worker = null;

        ReleaseRT(ref _resizeRGB);   ReleaseRT(ref _canvasRGB);
        ReleaseRT(ref _resizeDepth); ReleaseRT(ref _canvasDepth);
        ReleaseRT(ref _fullOutputRT);

        _imgTensorGPU?.Dispose();    _imgTensorGPU = null;
        _promptTensorGPU?.Dispose(); _promptTensorGPU = null;
    }

    // ---- Public API -------------------------------------------------------

    /// <summary> 推論の新規開始（前処理＋イテレータ初期化）。実行はまだ開始しない。 </summary>
    public bool Begin(RenderTexture rgbInput, RenderTexture depthInput, DateTime timestamp)
    {
        if (!IsInitialized || rgbInput == null || depthInput == null)
        {
            if (verboseLogging) Debug.LogWarning($"{logPrefix} Begin: invalid (IsInit={IsInitialized}, rgbNull={rgbInput==null}, depNull={depthInput==null})");
            return false;
        }
        if (_running)
        {
            if (verboseLogging) Debug.LogWarning($"{logPrefix} Begin: rejected (already running)");
            return false; // 単一ジョブ前提
        }

        // 初回のみレタボと資源確保
        if (!_letterboxParamsValid)
        {
            ComputeLetterboxParams(rgbInput.width, rgbInput.height,
                                   onnxWidth, onnxHeight, LETTERBOX_MULTIPLE,
                                   out _newW, out _newH, out _padX, out _padY);
            _dstW = onnxWidth; _dstH = onnxHeight;
            _letterboxParamsValid = true;
            EnsureResources();
            ResolveInputIndices();

            if (verboseLogging)
            {
                Debug.Log($"{logPrefix} Letterbox: src=({rgbInput.width}x{rgbInput.height}), dst=({_dstW}x{_dstH}), new=({_newW}x{_newH}), pad=({_padX},{_padY})");
                Debug.Log($"{logPrefix} RTs: canvasRGB={_canvasRGB.descriptor}, canvasDepth={_canvasDepth.descriptor}, out={(outputRT!=null ? outputRT.descriptor.ToString() : "null")}");
                Debug.Log($"{logPrefix} Inputs: imageIdx={_imageInputIndex}({_imageInputName}), promptIdx={_promptInputIndex}({_promptInputName})");
            }
        }

        // ---- 前処理（レタボ整形＋ToTensor）: CB で 1 回だけ実行 ----
        using (var cb = new CommandBuffer { name = "PromptDA Iterable Preprocess" })
        {
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

        // ---- イテレータ初期化：必ず worker.ScheduleIterable を使用 ----
        var feeds = new Tensor[2];
        feeds[_imageInputIndex]  = _imgTensorGPU;
        feeds[_promptInputIndex] = _promptTensorGPU;
        _iter = _worker.ScheduleIterable(feeds);

        _running       = true;
        CurrentTimestamp = timestamp;

        if (verboseLogging)
            Debug.Log($"{logPrefix} Begin: iterable started (frame={Time.frameCount}, t={Time.unscaledTime:0.000})");
        return true;
    }

    /// <summary> イテレータを steps 回だけ前進。CB は不要（内部でスケジュールされる）。 </summary>
    public void Step(int steps)
    {
        if (!_running || _iter == null || steps <= 0)
        {
            // if (verboseLogging && steps > 0)
            //     Debug.Log($"{logPrefix} Step: skipped (running={_running}, iterNull={_iter==null}, steps={steps})");
            return;
        }

        int advanced = 0;
        for (int k = 0; k < steps; k++)
        {
            bool hasMore = false;
            try { hasMore = _iter.MoveNext(); }
            catch (Exception e)
            {
                Debug.LogError($"{logPrefix} Step MoveNext exception: {e}");
                hasMore = false;
            }
            advanced++;

            if (!hasMore)
            {
                // ---- 全レイヤのスケジューリングが終わった。出力をRTへ書き出し ----
                var outRef = _worker.PeekOutput() as Tensor<float>;
                using (var cb = new CommandBuffer { name = "PromptDA Iterable Finalize" })
                {
                    // Clear destination to a known value (-1) to avoid stale pixels outside copy rect
                    if (outputRT != null)
                    {
                        cb.SetRenderTarget(outputRT);
                        cb.ClearRenderTarget(false, true, new Color(-1f, -1f, -1f, 1f));
                    }
                    cb.RenderToTexture(outRef, _fullOutputRT);
                    if (outputRT != null)
                    {
                        cb.CopyTexture(_fullOutputRT, 0,0, _padX,_padY, _newW,_newH, outputRT, 0,0, 0,0);
                    }
                    Graphics.ExecuteCommandBuffer(cb);
                }

                _running   = false;
                _finalizedJobId = _currentJobId;
                _finalizeFrame = Time.frameCount;
                if (verboseLogging)
                {
                    var p = HasDelayElapsed();
                    Debug.Log($"{logPrefix} Finalize: frame={Time.frameCount}, finalized={(_finalizedJobId!=System.Guid.Empty)}, passed={p}");
                }
                break;
            }
        }

        // if(_finalFence.passed)
        //     Debug.Log("passed");

        if (verboseLogging && _finalizedJobId != System.Guid.Empty)
        {
            var passed = HasDelayElapsed();
            Debug.Log($"{logPrefix} Step: advanced={advanced}, running={_running}, finalized={(_finalizedJobId!=System.Guid.Empty)}, passed={passed})");
        }
    }

    /// <summary> 完了後に内部ステートを初期化（次ジョブ用） </summary>
    public void ResetForNext()
    {
        if (_finalizedJobId == System.Guid.Empty || !HasDelayElapsed())
        {
            if (verboseLogging)
                Debug.Log($"{logPrefix} ResetForNext: ignored (finalized={(_finalizedJobId!=System.Guid.Empty)}, passed={HasDelayElapsed()})");
            return;
        }

        _iter = null;
        _running = false;
        _finalizedJobId = System.Guid.Empty;
        _completedJobId = System.Guid.Empty;
        _finalizeFrame = -1;

        if (verboseLogging)
            Debug.Log($"{logPrefix} ResetForNext: cleared (frame={Time.frameCount})");
    }

    // ---- Diagnostics -------------------------------------------------------

    // ---- 内部ヘルパ -------------------------------------------------------

    private void InitializeModelAndWorker()
    {
        if (promptDaOnnx == null)
        {
            Debug.LogError($"{logPrefix} Model is not assigned!");
            return;
        }
        _runtimeModel = ModelLoader.Load(promptDaOnnx);
        ResolveInputNamesFromModel(_runtimeModel);
        _worker = new Worker(_runtimeModel, backendType);
        IsInitialized = true;

        if (verboseLogging)
            Debug.Log($"{logPrefix} Model loaded. inputs={_runtimeModel.inputs.Count}, backend={backendType}");
    }

    private void ResolveInputNamesFromModel(Model model)
    {
        foreach (var inp in model.inputs)
        {
            var n = inp.name ?? string.Empty;
            if (n.IndexOf("image",  StringComparison.OrdinalIgnoreCase) >= 0) _imageInputName  = n;
            if (n.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("depth",  StringComparison.OrdinalIgnoreCase) >= 0) _promptInputName = n;
        }
    }

    private void ResolveInputIndices()
    {
        var inputs = _runtimeModel.inputs;
        for (int i = 0; i < inputs.Count; i++)
        {
            var nm = inputs[i].name ?? string.Empty;
            if (nm.IndexOf(_imageInputName,  StringComparison.OrdinalIgnoreCase) >= 0) _imageInputIndex  = i;
            if (nm.IndexOf(_promptInputName, StringComparison.OrdinalIgnoreCase) >= 0) _promptInputIndex = i;
        }
        if (verboseLogging)
            Debug.Log($"{logPrefix} ResolveInputIndices: imageIndex={_imageInputIndex}, promptIndex={_promptInputIndex}");
    }

    private void EnsureResources()
    {
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

    private static RenderTexture AllocRT(int w, int h, RenderTextureFormat fmt, bool enableRW)
    {
        var rt = new RenderTexture(w, h, 0, fmt)
        {
            enableRandomWrite = enableRW,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create();
        return rt;
    }

    private static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt == null) return;
        if (rt.IsCreated()) rt.Release();           
        UnityEngine.Object.Destroy(rt);
        rt = null;
    }

    private static void ComputeLetterboxParams(int srcW, int srcH, int dstW, int dstH, int multiple,
        out int newW, out int newH, out int padX, out int padY)
    {
        float scale = Mathf.Min((float)dstW / srcW, (float)dstH / srcH);
        newW = Mathf.Max(multiple, Mathf.FloorToInt(srcW * scale));
        newH = Mathf.Max(multiple, Mathf.FloorToInt(srcH * scale));
        newW -= newW % multiple;
        newH -= newH % multiple;
        padX = (dstW - newW) / 2;
        padY = (dstH -newH) / 2;
    }

    // ---- Completion helper (N-frame delay) --------------------------------
    private bool HasDelayElapsed()
    {
        if (_finalizedJobId == System.Guid.Empty || _finalizeFrame < 0) return false;
        int wait = Mathf.Max(0, completionFrameDelay);
        return (Time.frameCount - _finalizeFrame) >= wait;
    }

    // ---- Job ID (Guid) API ------------------------------------------------
    public void SetJobId(System.Guid jobId)
    {
        _currentJobId = jobId;
        if (verboseLogging) Debug.Log($"{logPrefix} SetJobId: {_currentJobId}");
    }

    /// <summary>
    /// Finalize済みのジョブを、遅延経過後にCompleteへ昇格（1回だけtrue）。
    /// </summary>
    public bool TryPromoteCompleted()
    {
        if (_finalizedJobId == System.Guid.Empty) return false;
        if (_completedJobId == _finalizedJobId) return false;
        if (!HasDelayElapsed()) return false;
        _completedJobId = _finalizedJobId;
        if (verboseLogging) Debug.Log($"{logPrefix} Complete: jobId={_completedJobId}");
        return true;
    }
}
