using System;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Sentis;

/// <summary>
/// PromptDA深度推定モデルの処理クラス
/// RenderTextureを受け取り、推論結果を返す単純な処理に特化
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
    // 多分いらない
    // private const float PROMPT_DEPTH_SCALE = 65.535f;
    private const int RGB_CHANNELS = 3;
    private const int DEPTH_CHANNELS = 1;
    
    // Runtime
    private Model _runtimeModel;
    private Worker[] _workers;
    private bool[] _workerBusy;
    
    // レタボ計算用キャッシュ
    private int _dstW, _dstH, _newW, _newH, _padX, _padY;
    private bool _letterboxParamsValid = false;
    
    // 入力名
    private string _imageInputName = "image";
    private string _promptInputName = "prompt_depth";

    public bool IsInitialized { get; private set; }

    void Awake()
    {
        InitializeModel();
        InitializeWorkers();
    }

    void InitializeModel()
    {
        if (promptDaOnnx == null)
        {
            Debug.LogError("PromptDA ONNX model is not assigned!");
            return;
        }

        var loadedModel = ModelLoader.Load(promptDaOnnx);
        ResolveInputNamesFromModel(loadedModel);
        
        // プロンプト入力のスケーリング付きモデルを作成
        var g = new FunctionalGraph();
        var finputs = g.AddInputs(loadedModel);
        var inputsMeta = loadedModel.inputs;
        var fwdInputs = new FunctionalTensor[finputs.Length];
        
        for (int i = 0; i < finputs.Length; i++) fwdInputs[i] = finputs[i];
        
        // prompt のインデックス特定
        int promptIdx = 0;
        for (int i = 0; i < inputsMeta.Count; i++)
        {
            var nm = inputsMeta[i].name ?? string.Empty;
            if (nm.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nm.IndexOf("depth", StringComparison.OrdinalIgnoreCase) >= 0)
            { promptIdx = i; break; }
        }
        
        fwdInputs[promptIdx] = finputs[promptIdx];
        
        var outs = Functional.Forward(loadedModel, fwdInputs);
        var depth = outs[0];
        _runtimeModel = g.Compile(depth);
        
        IsInitialized = true;
    }

    void InitializeWorkers()
    {
        _workers = new Worker[maxConcurrentWorkers];
        _workerBusy = new bool[maxConcurrentWorkers];
        
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            _workers[i] = new Worker(_runtimeModel, backendType);
            _workerBusy[i] = false;
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

    /// <summary>
    /// 非同期で推論を実行 (async/await版)
    /// </summary>
    public async Task<RenderTexture> ProcessAsync(RenderTexture rgbInput, RenderTexture depthInput)
    {
        if (!IsInitialized)
            throw new InvalidOperationException("Processor is not initialized");

        // 利用可能なワーカーを探す
        int workerIndex = GetAvailableWorkerIndex();
        if (workerIndex == -1)
            throw new InvalidOperationException("No available workers");

        return await ProcessInternalAsync(rgbInput, depthInput, workerIndex);
    }

    private async Task<RenderTexture> ProcessInternalAsync(RenderTexture rgbInput, RenderTexture depthInput, int workerIndex)
    {
        _workerBusy[workerIndex] = true;
        var worker = _workers[workerIndex];

        try
        {
            // レタボパラメータを計算（初回のみ）
            if (!_letterboxParamsValid)
            {
                ComputeLetterboxParams(rgbInput.width, rgbInput.height, 
                    onnxWidth, onnxHeight, LETTERBOX_MULTIPLE, out _newW, out _newH, out _padX, out _padY);
                _dstW = onnxWidth;
                _dstH = onnxHeight;
                _letterboxParamsValid = true;
            }

            // テンソルスコープを使用してメモリ安全に処理
            using var imageTensor = MakeImageTensorFromRT(rgbInput, _dstW, _dstH, _newW, _newH, _padX, _padY);
            using var promptTensor = MakePromptTensorFromDepthRT(depthInput, _dstW, _dstH, _newW, _newH, _padX, _padY);

            // 推論を開始
            var loadedInputs = _runtimeModel.inputs;
            int imgIdx = 0, promptIdx = 1;
            for (int i = 0; i < loadedInputs.Count; i++)
            {
                var nm = loadedInputs[i].name ?? string.Empty;
                if (nm.IndexOf(_imageInputName, StringComparison.OrdinalIgnoreCase) >= 0) imgIdx = i;
                if (nm.IndexOf(_promptInputName, StringComparison.OrdinalIgnoreCase) >= 0) promptIdx = i;
            }

            Tensor[] feeds = new Tensor[loadedInputs.Count];
            feeds[imgIdx] = imageTensor;
            feeds[promptIdx] = promptTensor;

            worker.Schedule(feeds);

            // 推論完了を待機（async/await版）
            await WaitForWorkerCompletionAsync(worker);

            // 結果を取得
            var output = worker.PeekOutput() as Tensor<float>;
            
            // 結果をRenderTextureに変換
            return CreateResultTexture(output);
        }
        finally
        {
            _workerBusy[workerIndex] = false;
        }
    }

    private bool IsWorkerComplete(Worker worker)
    {
        try
        {
            var output = worker.PeekOutput();
            return output != null;
        }
        catch
        {
            return false;
        }
    }

    private async Task WaitForWorkerCompletionAsync(Worker worker)
    {
        while (!IsWorkerComplete(worker))
        {
            await Task.Yield(); // 他のタスクに制御を譲る
        }
    }

    private int GetAvailableWorkerIndex()
    {
        for (int i = 0; i < _workerBusy.Length; i++)
        {
            if (!_workerBusy[i]) return i;
        }
        return -1;
    }

    private RenderTexture CreateResultTexture(Tensor<float> output)
    {
        // レタボを削除して出力
        var fullOutputRT = RenderTexture.GetTemporary(_dstW, _dstH, 0, RenderTextureFormat.RFloat);
        TextureConverter.RenderToTexture(output, fullOutputRT);

        // ROI部分のみを抽出
        var resultRT = RenderTexture.GetTemporary(_newW, _newH, 0, RenderTextureFormat.RFloat);
        Graphics.CopyTexture(
            fullOutputRT, 0, 0, _padX, _padY, _newW, _newH,
            resultRT, 0, 0, 0, 0
        );

        // 一時テクスチャを解放
        RenderTexture.ReleaseTemporary(fullOutputRT);

        // resultRTは呼び出し側で管理（自動解放 or Estimator側でオプション解放）
        return resultRT;
    }

    // ===== Helper Methods =====

    private Tensor<float> MakeImageTensorFromRT(RenderTexture srcRgb, int dstW, int dstH, int newW, int newH, int padX, int padY)
    {
        var rtResized = RenderTexture.GetTemporary(newW, newH, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(srcRgb, rtResized);

        var rtCanvas = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32);
        Graphics.CopyTexture(rtResized, 0, 0, 0, 0, newW, newH, rtCanvas, 0, 0, padX, padY);

        var t = new Tensor<float>(new TensorShape(1, RGB_CHANNELS, dstH, dstW));
        var transform = new TextureTransform().SetDimensions(dstW, dstH);
        TextureConverter.ToTensor(rtCanvas, t, transform);

        RenderTexture.ReleaseTemporary(rtResized);
        RenderTexture.ReleaseTemporary(rtCanvas);
        
        return t;
    }

    private Tensor<float> MakePromptTensorFromDepthRT(RenderTexture srcDepth, int dstW, int dstH, int newW, int newH, int padX, int padY)
    {
        var rtResized = RenderTexture.GetTemporary(newW, newH, 0, RenderTextureFormat.RFloat);
        Graphics.Blit(srcDepth, rtResized);

        var rtCanvas = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.RFloat);
        Graphics.CopyTexture(rtResized, 0, 0, 0, 0, newW, newH, rtCanvas, 0, 0, padX, padY);

        var t = new Tensor<float>(new TensorShape(1, DEPTH_CHANNELS, dstH, dstW));
        var transform = new TextureTransform().SetDimensions(dstW, dstH);
        TextureConverter.ToTensor(rtCanvas, t, transform);

        RenderTexture.ReleaseTemporary(rtResized);
        RenderTexture.ReleaseTemporary(rtCanvas);
        
        return t;
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

    void OnDestroy()
    {
        // ワーカーのクリーンアップ
        if (_workers != null)
        {
            foreach (var worker in _workers)
            {
                worker?.Dispose();
            }
        }
    }
}
