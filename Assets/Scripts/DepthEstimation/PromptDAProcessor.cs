using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Sentis;

/// <summary>
/// PromptDA深度推定モデルの処理クラス（GPU完結版）
/// - 入力: RGB/Depth の RenderTexture
/// - 推論: CommandBuffer に ToTensor -> ScheduleWorker -> RenderToTexture をキューイング
/// - 出力: レタボ除去後の RenderTexture（GPU 所有）
///
/// 動作要件:
///   * Unity Sentis 2.1.0+
///     - CommandBuffer スケジュール: CommandBufferWorkerExtensions.ScheduleWorker(...)
///     - Texture <-> Tensor 変換: TextureConverter の CommandBuffer 拡張
///   * BackendType.GPUCompute 推奨
/// </summary>
public class PromptDAProcessor : MonoBehaviour
{
    [Header("Model Configuration")]
    [SerializeField] private ModelAsset promptDaOnnx;

    [Header("ONNX Settings")]
    [SerializeField] private int onnxWidth = 3836;
    [SerializeField] private int onnxHeight = 2156;

    [Header("Processing Settings")]
    [SerializeField] private int maxConcurrentWorkers = 2;
    [SerializeField] private BackendType backendType = BackendType.GPUCompute;

    // 定数
    private const int LETTERBOX_MULTIPLE = 14;
    private const int RGB_CHANNELS = 3;
    private const int DEPTH_CHANNELS = 1;

    // Runtime
    private Model _runtimeModel;
    private Worker[] _workers;
    private bool[] _workerBusy;

    // per-worker GPUリソース
    private CommandBuffer[] _cbs;

    private RenderTexture[] _resizeRGB;    // newW x newH, ARGB32
    private RenderTexture[] _canvasRGB;    // dstW x dstH, ARGB32（パディング込み）
    private RenderTexture[] _resizeDepth;  // newW x newH, RFloat
    private RenderTexture[] _canvasDepth;  // dstW x dstH, RFloat（パディング込み）

    private Tensor<float>[] _imgTensorGPU;    // [1,3,H,W]
    private Tensor<float>[] _promptTensorGPU; // [1,1,H,W]

    private RenderTexture[] _fullOutputRT; // dstW x dstH, RFloat（モデル出力）
    private RenderTexture[] _resultRT;     // newW x newH, RFloat（レタボ除去後）

    // レタボ計算用キャッシュ
    private int _dstW, _dstH, _newW, _newH, _padX, _padY;
    private bool _letterboxParamsValid = false;

    // 入力名
    private string _imageInputName = "image";
    private string _promptInputName = "prompt_depth";
    
    // 入力インデックス（PromptDATestと同様の配列ベース実行用）
    private int _imageInputIndex = 0;
    private int _promptInputIndex = 1;

    public bool IsInitialized { get; private set; }

    // ===== Unity Hooks =====

    void Awake()
    {
        InitializeModel();
        InitializeWorkers();
    }

    void OnDestroy()
    {
        // ワーカー破棄
        if (_workers != null)
        {
            foreach (var w in _workers) w?.Dispose();
        }

        // コマンドバッファ
        if (_cbs != null)
        {
            foreach (var cb in _cbs) cb?.Release();
        }

        // GPUリソース破棄
        ReleaseRTArray(_resizeRGB);
        ReleaseRTArray(_canvasRGB);
        ReleaseRTArray(_resizeDepth);
        ReleaseRTArray(_canvasDepth);
        ReleaseRTArray(_fullOutputRT);
        ReleaseRTArray(_resultRT);

        // Tensor破棄
        DisposeTensorArray(_imgTensorGPU);
        DisposeTensorArray(_promptTensorGPU);
    }

    // ===== Public API (外部IFは維持) =====

    /// <summary>
    /// 非同期で推論を実行（GPU完結。CPUへのReadbackは行いません）
    /// </summary>
    public async Task<RenderTexture> ProcessAsync(RenderTexture rgbInput, RenderTexture depthInput)
    {
        if (!IsInitialized) throw new InvalidOperationException("Processor is not initialized");

        // 初回のみレタボ計算
        if (!_letterboxParamsValid)
        {
            ComputeLetterboxParams(rgbInput.width, rgbInput.height,
                onnxWidth, onnxHeight, LETTERBOX_MULTIPLE,
                out _newW, out _newH, out _padX, out _padY);
            _dstW = onnxWidth;
            _dstH = onnxHeight;
            _letterboxParamsValid = true;

            // レタボ確定後に per-worker リソースを確保
            EnsurePerWorkerResources();
            // 各ワーカーへ固定入力Tensorをバインド（以降は ToTensor で内容だけ更新）
            // 元モデルの入力順序に基づいてインデックスを特定
            int imgIdx = 0, promptIdx = 1;
            var loadedInputs = _runtimeModel.inputs; // ランタイムモデルの入力情報
            for (int i = 0; i < loadedInputs.Count; i++)
            {
                var nm = loadedInputs[i].name ?? string.Empty;
                if (nm.IndexOf(_imageInputName, StringComparison.OrdinalIgnoreCase) >= 0) imgIdx = i;
                if (nm.IndexOf(_promptInputName, StringComparison.OrdinalIgnoreCase) >= 0) promptIdx = i;
            }
            
            // 各ワーカーに入力順序を記録
            _imageInputIndex = imgIdx;
            _promptInputIndex = promptIdx;
        }

        // 利用可能なワーカーを確保
        int workerIndex = GetAvailableWorkerIndex();
        if (workerIndex == -1) throw new InvalidOperationException("No available workers");

        _workerBusy[workerIndex] = true;
        try
        {
            // 1) CBをクリア
            var cb = _cbs[workerIndex];
            cb.Clear();

            // 2) レタボ用のリサイズ＆配置（GPU）
            // RGB
            cb.Blit(rgbInput, _resizeRGB[workerIndex]);
            cb.CopyTexture(
                _resizeRGB[workerIndex], 0, 0, 0, 0, _newW, _newH,
                _canvasRGB[workerIndex], 0, 0, _padX, _padY
            );
            // Depth
            cb.Blit(depthInput, _resizeDepth[workerIndex]);
            cb.CopyTexture(
                _resizeDepth[workerIndex], 0, 0, 0, 0, _newW, _newH,
                _canvasDepth[workerIndex], 0, 0, _padX, _padY
            );

            // 3) Texture -> Tensor （GPU, NCHW）
            var tfRGB = new TextureTransform()
                .SetDimensions(_dstW, _dstH, RGB_CHANNELS)
                .SetTensorLayout(TensorLayout.NCHW);
            var tfDEP = new TextureTransform()
                .SetDimensions(_dstW, _dstH, DEPTH_CHANNELS)
                .SetTensorLayout(TensorLayout.NCHW);

            // CommandBuffer拡張（Sentis 2.1+）
            cb.ToTensor(_canvasRGB[workerIndex], _imgTensorGPU[workerIndex], tfRGB);
            cb.ToTensor(_canvasDepth[workerIndex], _promptTensorGPU[workerIndex], tfDEP);

            // 4) 推論をCBにスケジュール（非ブロッキング）
            // PromptDATestと同様に配列ベースでSchedule
            var feeds = new Tensor[2]; // 2入力モデル
            feeds[_imageInputIndex] = _imgTensorGPU[workerIndex];
            feeds[_promptInputIndex] = _promptTensorGPU[workerIndex];
            cb.ScheduleWorker(_workers[workerIndex], feeds);

            // 5) 出力Tensor -> RT（GPU、RFloat）。その後 ROI 切り出し
            var outRef = _workers[workerIndex].PeekOutput() as Tensor<float>; // 非ブロッキング参照
            cb.RenderToTexture(outRef, _fullOutputRT[workerIndex]);
            cb.CopyTexture(
                _fullOutputRT[workerIndex], 0, 0, _padX, _padY, _newW, _newH,
                _resultRT[workerIndex], 0, 0, 0, 0
            );

            // 6) 実行 + GPU完了まで非同期待機
#if UNITY_EDITOR
            var fence = cb.CreateGraphicsFence(
                GraphicsFenceType.CPUSynchronisation,
                SynchronisationStageFlags.ComputeProcessing);
#else
            var fence = cb.CreateGraphicsFence(
                GraphicsFenceType.AsyncQueueSynchronisation,
                SynchronisationStageFlags.ComputeProcessing);
#endif
            Graphics.ExecuteCommandBuffer(cb);

            // CPUはブロックせず、GPUがフェンスを通過するまで譲るだけ
            while (!fence.passed)
                await Task.Yield();

            return _resultRT[workerIndex];

        }
        finally
        {
            _workerBusy[workerIndex] = false; // GPU完了後に解放
        }
    }

    // ===== 初期化 =====

    void InitializeModel()
    {
        if (promptDaOnnx == null)
        {
            Debug.LogError("PromptDA ONNX model is not assigned!");
            return;
        }

        var loadedModel = ModelLoader.Load(promptDaOnnx);
        ResolveInputNamesFromModel(loadedModel);

        // 入力は既にメートル単位なので、そのまま使用
        _runtimeModel = loadedModel;
        IsInitialized = true;
    }

    void InitializeWorkers()
    {
        int n = Mathf.Max(1, maxConcurrentWorkers);
        _workers = new Worker[n];
        _workerBusy = new bool[n];
        _cbs = new CommandBuffer[n];

        for (int i = 0; i < n; i++)
        {
            _workers[i] = new Worker(_runtimeModel, backendType); // 推奨: GPUCompute
            _workerBusy[i] = false;
            _cbs[i] = new CommandBuffer { name = $"PromptDA GPU Pipe [{i}]" };
        }
    }

    void ResolveInputNamesFromModel(Model model)
    {
        var inputs = model.inputs;
        foreach (var inp in inputs)
        {
            var n = inp.name ?? string.Empty;
            if (n.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0)
                _imageInputName = n;
            if (n.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("depth", StringComparison.OrdinalIgnoreCase) >= 0)
                _promptInputName = n;
        }
    }

    // ===== 内部ヘルパ =====

    private int GetAvailableWorkerIndex()
    {
        for (int i = 0; i < _workerBusy.Length; i++)
            if (!_workerBusy[i]) return i;
        return -1;
    }

    private void EnsurePerWorkerResources()
    {
        int n = _workers.Length;

        _resizeRGB    = new RenderTexture[n];
        _canvasRGB    = new RenderTexture[n];
        _resizeDepth  = new RenderTexture[n];
        _canvasDepth  = new RenderTexture[n];
        _fullOutputRT = new RenderTexture[n];
        _resultRT     = new RenderTexture[n];

        _imgTensorGPU    = new Tensor<float>[n];
        _promptTensorGPU = new Tensor<float>[n];

        for (int i = 0; i < n; i++)
        {
            // 入力レタボ用
            _resizeRGB[i] = AllocRT(_newW, _newH, RenderTextureFormat.ARGB32, false);
            _canvasRGB[i] = AllocRT(_dstW, _dstH, RenderTextureFormat.ARGB32, false);

            _resizeDepth[i] = AllocRT(_newW, _newH, RenderTextureFormat.RFloat, false);
            _canvasDepth[i] = AllocRT(_dstW, _dstH, RenderTextureFormat.RFloat, false);

            // 出力
            _fullOutputRT[i] = AllocRT(_dstW, _dstH, RenderTextureFormat.RFloat, true); // compute書き込み用
            _resultRT[i]     = AllocRT(_newW, _newH, RenderTextureFormat.RFloat, true); // compute書き込み用

            // 入力Tensor（NCHW）
            _imgTensorGPU[i]    = new Tensor<float>(new TensorShape(1, RGB_CHANNELS, _dstH, _dstW));
            _promptTensorGPU[i] = new Tensor<float>(new TensorShape(1, DEPTH_CHANNELS, _dstH, _dstW));
        }
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

    private static void ReleaseRTArray(RenderTexture[] arr)
    {
        if (arr == null) return;
        foreach (var rt in arr)
        {
            if (rt == null) continue;
            if (rt.IsCreated()) rt.Release();
            UnityEngine.Object.Destroy(rt);
        }
    }

    private static void DisposeTensorArray(Tensor<float>[] arr)
    {
        if (arr == null) return;
        foreach (var t in arr) t?.Dispose();
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
        padY = (dstH - newH) / 2;
    }
}
