using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Sentis;

public class PromptDATest : MonoBehaviour
{
    [Header("Models / IO")]
    [SerializeField] private ModelAsset promptDaOnnx;              // 2入力: "image", "prompt_depth"（Python版と同名）
    [SerializeField] private Texture2D testImg;               // RGB
    [SerializeField] private Texture2D testDepth;             // 単一ch16bit(mm)想定（R16）
    [SerializeField] private RenderTexture visualizeTexture;  // 出力先（RFloat, サイズ=_newW×_newH）

    [Header("ONNX canvas size (letterbox target)")]
    [SerializeField] private int onnxWidth  = 3836;           // Pythonランタイム既定に合わせる
    [SerializeField] private int onnxHeight = 2156;

    // Runtime
    Model  _runtimeModel;
    Worker _worker;

    Tensor<float> _imageTensor;   // (1,3,_dstH,_dstW)
    Tensor<float> _promptTensor;  // (1,1,_dstH,_dstW) ※ここでは0..1（R16正規化値）

    // Letterbox / temp
    int _dstW, _dstH, _newW, _newH, _padX, _padY;
    RenderTexture _fullOutputRT; // (dstW,dstH,RFloat)

    // 入力名（Model.inputs から取得）
    string _imageInputName  = "image";
    string _promptInputName = "prompt_depth";

    private Model _loadedModel;

    void Start()
    {
        _loadedModel = ModelLoader.Load(promptDaOnnx);
        // モデル入力名をModel.inputs（小文字）から確定
        ResolveInputNamesFromModel(_loadedModel);

        // プロンプト入力のみ 0..1 → mm（×65535）に戻す前処理を FunctionalGraph で挿入
        InitializeModel_WithPromptDepthToMeters();
        
        PrepareInputs();
        RunOnce();
        RenderOutputCropped(); // レタボ削除して visualizeTexture へ
    }

    void ResolveInputNamesFromModel(Model model)
    {
        // 名前が分かっているならこの処理は省略可（Pythonと同名: "image", "prompt_depth"）
        var inputs = model.inputs; // List<Model.Input>
        foreach (var inp in inputs)
        {
            var n = inp.name ?? string.Empty;
            if (n.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0)
                _imageInputName = n;
            if (n.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("depth",  StringComparison.OrdinalIgnoreCase) >= 0)
                _promptInputName = n;
        }
    }

    // void InitializeModel_WithPromptRescaleU01_to_mm()
    // {
    //     var g = new FunctionalGraph();

    //     // 元モデルの全入力をFunctionalとして追加
    //     var finputs = g.AddInputs(_loadedModel); // FunctionalTensor[]

    //     // prompt入力だけ 0..1 → mm(×65535) に戻してからForward
    //     // インデックスは Model.inputs の順と一致
    //     var inputsMeta = _loadedModel.inputs;
    //     var fwdInputs = new FunctionalTensor[finputs.Length];
    //     for (int i = 0; i < finputs.Length; i++) fwdInputs[i] = finputs[i];

    //     // prompt のインデックスを特定
    //     int promptIdx = 0;
    //     for (int i = 0; i < inputsMeta.Count; i++)
    //     {
    //         var nm = inputsMeta[i].name ?? string.Empty;
    //         if (nm.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
    //             nm.IndexOf("depth",  StringComparison.OrdinalIgnoreCase) >= 0)
    //         { promptIdx = i; break; }
    //     }
    //     fwdInputs[promptIdx] = finputs[promptIdx] * 65535f; // R16正規化値→mm

    //     // 前進・出力（Python側は out[0,0,...] ＝ (1,1,H,W) 前提）
    //     var outs  = Functional.Forward(_loadedModel, fwdInputs);
    //     var depth = outs[0]; // 既に (1,1,H,W) を想定：Unsqueeze等は不要

    //     _runtimeModel = g.Compile(depth);
    //     _worker = new Worker(_runtimeModel, BackendType.GPUCompute);
    // }
    // 旧: InitializeModel_WithPromptRescaleU01_to_mm
    void InitializeModel_WithPromptDepthToMeters()
    {
        var g = new FunctionalGraph();
        var finputs = g.AddInputs(_loadedModel);

        var inputsMeta = _loadedModel.inputs;
        var fwdInputs = new FunctionalTensor[finputs.Length];
        for (int i = 0; i < finputs.Length; i++) fwdInputs[i] = finputs[i];

        // prompt のインデックス特定
        int promptIdx = 0;
        for (int i = 0; i < inputsMeta.Count; i++)
        {
            var nm = inputsMeta[i].name ?? string.Empty;
            if (nm.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nm.IndexOf("depth",  StringComparison.OrdinalIgnoreCase) >= 0)
            { promptIdx = i; break; }
        }

        // ★ここが本質の修正★
        // R16サンプリング値(0..1) → meters: (value * 65535) / 1000 = value * 65.535
        fwdInputs[promptIdx] = finputs[promptIdx] * 65.535f;

        var outs  = Functional.Forward(_loadedModel, fwdInputs);
        var depth = outs[0]; // (1,1,H,W) 想定
        _runtimeModel = g.Compile(depth);
        _worker= new Worker(_runtimeModel, BackendType.GPUCompute);
    } 


    void PrepareInputs()
    {
        _dstW = onnxWidth;
        _dstH = onnxHeight;

        ComputeLetterboxParams(testImg.width, testImg.height, _dstW, _dstH, 14,
            out _newW, out _newH, out _padX, out _padY);

        _imageTensor  = MakeImageTensorLetterboxed(testImg,  _dstW, _dstH, _newW, _newH, _padX, _padY);
        _promptTensor = MakePromptTensorFromDepth16mm(testDepth, _dstW, _dstH, _newW, _newH, _padX, _padY);
        // ※ _promptTensor はここでは 0..1（R16正規化）；グラフ内で×65535してmm化
    }

    void RunOnce()
    {
        // 元モデルの入力メタ（順序）を使ってフィード配列を作る
        var loadedInputs = _loadedModel.inputs; // List<Model.Input>
        int n = loadedInputs.Count;

        // 画像/プロンプトのインデックス（見つからなければフォールバック0/1）
        int imgIdx = 0, promptIdx = 1;
        for (int i = 0; i < n; i++)
        {
            var nm = loadedInputs[i].name ?? string.Empty;
            if (nm.IndexOf(_imageInputName,  StringComparison.OrdinalIgnoreCase) >= 0)  imgIdx = i;
            if (nm.IndexOf(_promptInputName, StringComparison.OrdinalIgnoreCase) >= 0)  promptIdx = i;
        }

        // フィード配列を「元モデルの順序」で並べる
        Tensor[] feeds = new Tensor[n];
        feeds[imgIdx]    = _imageTensor;   // (1,3,H,W)
        feeds[promptIdx] = _promptTensor;  // (1,1,H,W) ※グラフ内で×65535してmmへ

        // 名前指定は使わず、順序指定で実行
        _worker.Schedule(feeds);
    }


    void RenderOutputCropped()
    {
        // フル（レタボ込み）を書き出し
        _fullOutputRT = CreateRT(_dstW, _dstH, RenderTextureFormat.RFloat);
        var output = _worker.PeekOutput() as Tensor<float>; // (1,1,_dstH,_dstW) 想定（単位: m）
        TextureConverter.RenderToTexture(output, _fullOutputRT);
        DepthRTInspector.DumpStats(_fullOutputRT);

        // ROIのみを可視化先へコピー（= レタボ削除）
        // visualizeTexture は (_newW×_newH, RFloat) を事前に設定
        Graphics.CopyTexture(
            _fullOutputRT, 0, 0, _padX, _padY, _newW, _newH,
            visualizeTexture, 0, 0, 0, 0
        );
         DepthRTInspector.DumpStats(visualizeTexture);
    }

    // ===== Helpers =====

    // 16bit単ch(mm) → 0..1で取り込み → レタボ → Tensor(1,1,H,W)
    // mmへの復元（×65535）はグラフ内で適用済み
    static Tensor<float> MakePromptTensorFromDepth16mm(
        Texture2D srcDepth16mm, int dstW, int dstH, int newW, int newH, int padX, int padY)
    {
        var rtResized = CreateRT(newW, newH, RenderTextureFormat.RFloat);
        Graphics.Blit(srcDepth16mm, rtResized); // R16→サンプル時に0..1へ

        var rtCanvas  = CreateRT(dstW, dstH, RenderTextureFormat.RFloat);
        Graphics.CopyTexture(rtResized, 0, 0, 0, 0, newW, newH, rtCanvas, 0, 0, padX, padY);

        var t = new Tensor<float>(new TensorShape(1, 1, dstH, dstW));
        var transform = new TextureTransform().SetDimensions(dstW, dstH);
        TextureConverter.ToTensor(rtCanvas, t, transform);

        ReleaseAndDestroy(rtResized);
        ReleaseAndDestroy(rtCanvas);
        return t;
    }

    static Tensor<float> MakeImageTensorLetterboxed(
        Texture2D srcRgb, int dstW, int dstH, int newW, int newH, int padX, int padY)
    {
        var rtResized = CreateRT(newW, newH, RenderTextureFormat.ARGB32);
        Graphics.Blit(srcRgb, rtResized);

        var rtCanvas  = CreateRT(dstW, dstH, RenderTextureFormat.ARGB32);
        Graphics.CopyTexture(rtResized, 0, 0, 0, 0, newW, newH, rtCanvas, 0, 0, padX, padY);

        var t = new Tensor<float>(new TensorShape(1, 3, dstH, dstW));
        var transform = new TextureTransform().SetDimensions(dstW, dstH);
        TextureConverter.ToTensor(rtCanvas, t, transform);

        ReleaseAndDestroy(rtResized);
        ReleaseAndDestroy(rtCanvas);
        return t;
    }

    static void ComputeLetterboxParams(int srcW, int srcH, int dstW, int dstH, int multiple,
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

    static RenderTexture CreateRT(int w, int h, RenderTextureFormat fmt)
    {
        var rt = new RenderTexture(w, h, 0, fmt);
        rt.Create();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, Color.black);
        RenderTexture.active = prev;
        return rt;
    }

    static void ReleaseAndDestroy(RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        UnityEngine.Object.Destroy(rt);
    }

    void OnDestroy()
    {
        _worker?.Dispose();
        _imageTensor?.Dispose();
        _promptTensor?.Dispose();
        if (_fullOutputRT != null) { _fullOutputRT.Release(); Destroy(_fullOutputRT); _fullOutputRT = null; }
    }
}

