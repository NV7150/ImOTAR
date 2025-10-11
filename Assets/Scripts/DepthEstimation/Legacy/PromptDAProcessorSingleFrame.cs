// using System;
// using UnityEngine;
// using UnityEngine.Rendering;


// /// <summary>
// /// PromptDA深度推定（GPU完結・非待機）
// /// Sentis 2.1.x / BackendType.GPUCompute 推奨
// /// - TrySubmit: CB に ToTensor → ScheduleWorker → RenderToTexture → ROI → Fence を積んで即 return
// /// - フェンスは Async 対応なら AsyncQueue、非対応なら CPU 同期タイプを発行（.passed が常に安全）
// /// </summary>
// public class PromptDAProcessorSingleFrame  : MonoBehaviour
// {
//     [Header("Model Configuration")]
//     [SerializeField] private Unity.InferenceEngine.ModelAsset promptDaOnnx;

//     [Header("ONNX Settings")]
//     [SerializeField] private int onnxWidth = 3836;
//     [SerializeField] private int onnxHeight = 2156;

//     [Header("Processing Settings")]
//     [SerializeField] private int maxConcurrentWorkers = 2;
//     [SerializeField] private Unity.InferenceEngine.BackendType backendType = Unity.InferenceEngine.BackendType.GPUCompute;

//     private const int LETTERBOX_MULTIPLE = 14;
//     private const int RGB_CHANNELS = 3;
//     private const int DEPTH_CHANNELS = 1;

//     private Unity.InferenceEngine.Model _runtimeModel;
//     private Unity.InferenceEngine.Worker[] _workers;
//     private bool[] _workerBusy;
//     private CommandBuffer[] _cbs;

//     private RenderTexture[] _resizeRGB;
//     private RenderTexture[] _canvasRGB;
//     private RenderTexture[] _resizeDepth;
//     private RenderTexture[] _canvasDepth;
//     private Unity.InferenceEngine.Tensor<float>[] _imgTensorGPU;
//     private Unity.InferenceEngine.Tensor<float>[] _promptTensorGPU;
//     private RenderTexture[] _fullOutputRT;
//     private RenderTexture[] _resultRT;

//     private int _dstW, _dstH, _newW, _newH, _padX, _padY;
//     private bool _letterboxParamsValid = false;

//     private string _imageInputName = "image";
//     private string _promptInputName = "prompt_depth";
//     private int _imageInputIndex = 0;
//     private int _promptInputIndex = 1;

//     private bool _supportsAsyncCompute;
//     public bool SupportsAsyncCompute => _supportsAsyncCompute;
//     public bool IsInitialized { get; private set; }

//     // GPU完了検出用トークン
//     public struct InflightJob
//     {
//         public DateTime timestamp;
//         public RenderTexture result;  // RFloat, レタボ除去後
//         public GraphicsFence fence;   // Async対応: AsyncQueue / 非対応: CPUSync
//         public int workerIndex;
//     }

//     void Awake()
//     {
//         _supportsAsyncCompute = SystemInfo.supportsAsyncCompute;
//         InitializeModel();
//         InitializeWorkers();
//     }

//     void OnDestroy()
//     {
//         if (_workers != null) foreach (var w in _workers) w?.Dispose();
//         if (_cbs != null)     foreach (var cb in _cbs)     cb?.Release();

//         ReleaseRTArray(_resizeRGB);
//         ReleaseRTArray(_canvasRGB);
//         ReleaseRTArray(_resizeDepth);
//         ReleaseRTArray(_canvasDepth);
//         ReleaseRTArray(_fullOutputRT);
//         ReleaseRTArray(_resultRT);

//         DisposeTensorArray(_imgTensorGPU);
//         DisposeTensorArray(_promptTensorGPU);
//     }

//     /// <summary>
//     /// 推論を非待機で提出（CPUは待たない）
//     /// </summary>
//     public bool TrySubmit(RenderTexture rgbInput, RenderTexture depthInput, DateTime timestamp, out InflightJob job)
//     {
//         job = default;
//         if (!IsInitialized || rgbInput == null || depthInput == null) return false;

//         // 初回のみレタボ確定と資源割当
//         if (!_letterboxParamsValid)
//         {
//             ComputeLetterboxParams(rgbInput.width, rgbInput.height,
//                                    onnxWidth, onnxHeight, LETTERBOX_MULTIPLE,
//                                    out _newW, out _newH, out _padX, out _padY);
//             _dstW = onnxWidth; _dstH = onnxHeight;
//             _letterboxParamsValid = true;
//             EnsurePerWorkerResources();

//             // 入力インデックス解決
//             var inputs = _runtimeModel.inputs;
//             for (int j = 0; j < inputs.Count; j++)
//             {
//                 var nm = inputs[j].name ?? string.Empty;
//                 if (nm.IndexOf(_imageInputName,  StringComparison.OrdinalIgnoreCase) >= 0) _imageInputIndex  = j;
//                 if (nm.IndexOf(_promptInputName, StringComparison.OrdinalIgnoreCase) >= 0) _promptInputIndex = j;
//             }
//         }

//         int i = GetAvailableWorkerIndex();
//         if (i < 0) return false;
//         _workerBusy[i] = true;

//         var cb = _cbs[i];
//         cb.Clear();

//         // 1) レタボ整形
//         cb.Blit(rgbInput, _resizeRGB[i]);
//         cb.CopyTexture(_resizeRGB[i],  0,0, 0,0, _newW,_newH, _canvasRGB[i],  0,0, _padX,_padY);
//         cb.Blit(depthInput, _resizeDepth[i]);
//         cb.CopyTexture(_resizeDepth[i], 0,0, 0,0, _newW,_newH, _canvasDepth[i],0,0, _padX,_padY);

//         // 2) Texture -> Tensor（NCHW）
//         var tfRGB = new TextureTransform().SetDimensions(_dstW, _dstH, RGB_CHANNELS).SetTensorLayout(Unity.InferenceEngine.TensorLayout.NCHW);
//         var tfDEP = new TextureTransform().SetDimensions(_dstW, _dstH, DEPTH_CHANNELS).SetTensorLayout(Unity.InferenceEngine.TensorLayout.NCHW);
//         cb.ToTensor(_canvasRGB[i],  _imgTensorGPU[i],    tfRGB);
//         cb.ToTensor(_canvasDepth[i], _promptTensorGPU[i], tfDEP);

//         // 3) 推論（CBへ積むだけ）
//         var feeds = new Tensor[2];
//         feeds[_imageInputIndex]  = _imgTensorGPU[i];
//         feeds[_promptInputIndex] = _promptTensorGPU[i];
//         Unity.InferenceEngine.CommandBufferWorkerExtensions.ScheduleWorker(        cb, _workers[i], feeds);

//         // 4) 出力 Tensor -> RT（RFloat）→ ROI でレタボ除去
//         var outRef = _workers[i].PeekOutput() as Unity.InferenceEngine.Tensor<float>;
//         cb.RenderToTexture(outRef, _fullOutputRT[i]);
//         cb.CopyTexture(_fullOutputRT[i], 0,0, _padX,_padY, _newW,_newH, _resultRT[i], 0,0, 0,0);

//         // 5) フェンス作成（Async有無でタイプ切替：.passed が常に使える）
//         var type  = _supportsAsyncCompute ? GraphicsFenceType.AsyncQueueSynchronisation
//                                           : GraphicsFenceType.CPUSynchronisation;
//         var fence = cb.CreateGraphicsFence(type, SynchronisationStageFlags.ComputeProcessing);

//         // 6) 実行投入（CPUは即時復帰）
//         Graphics.ExecuteCommandBuffer(cb);

//         job = new InflightJob
//         {
//             timestamp = timestamp,
//             result    = _resultRT[i],
//             fence     = fence,
//             workerIndex = i
//         };
//         return true;
//     }

//     /// <summary> フェンス通過済みならワーカーを解放（非待機） </summary>
//     public void ReleaseWorkerIfComplete(in InflightJob job)
//     {
//         if (job.fence.passed)
//         {
//             _workerBusy[job.workerIndex] = false;
//         }
//     }

//     // ===== 初期化/資源管理 =====

//     private void InitializeModel()
//     {
//         if (promptDaOnnx == null)
//         {
//             Debug.LogError("PromptDA ONNX model is not assigned!");
//             return;
//         }
//         var loaded = Unity.InferenceEngine.ModelLoader.Load(promptDaOnnx);
//         ResolveInputNamesFromModel(loaded);
//         _runtimeModel = loaded;
//         IsInitialized = true;
//     }

//     private void InitializeWorkers()
//     {
//         int n = Mathf.Max(1, maxConcurrentWorkers);
//         _workers = new Unity.InferenceEngine.Worker[n];
//         _workerBusy = new bool[n];
//         _cbs = new CommandBuffer[n];

//         for (int i = 0; i < n; i++)
//         {
//             _workers[i] = new Unity.InferenceEngine.Worker(_runtimeModel, backendType);
//             _workerBusy[i] = false;
//             _cbs[i] = new CommandBuffer { name = $"PromptDA GPU Pipe [{i}]" };
//         }
//     }

//     private void ResolveInputNamesFromModel(Unity.InferenceEngine.Model model)
//     {
//         foreach (var inp in model.inputs)
//         {
//             var n = inp.name ?? string.Empty;
//             if (n.IndexOf("image",  StringComparison.OrdinalIgnoreCase) >= 0) _imageInputName  = n;
//             if (n.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
//                 n.IndexOf("depth",  StringComparison.OrdinalIgnoreCase) >= 0) _promptInputName = n;
//         }
//     }

//     private int GetAvailableWorkerIndex()
//     {
//         for (int i = 0; i < _workerBusy.Length; i++)
//             if (!_workerBusy[i]) return i;
//         return -1;
//     }

//     private void EnsurePerWorkerResources()
//     {
//         int n = _workers.Length;

//         _resizeRGB    = new RenderTexture[n];
//         _canvasRGB    = new RenderTexture[n];
//         _resizeDepth  = new RenderTexture[n];
//         _canvasDepth  = new RenderTexture[n];
//         _fullOutputRT = new RenderTexture[n];
//         _resultRT     = new RenderTexture[n];

//         _imgTensorGPU    = new Unity.InferenceEngine.Tensor<float>[n];
//         _promptTensorGPU = new Unity.InferenceEngine.Tensor<float>[n];

//         for (int i = 0; i < n; i++)
//         {
//             _resizeRGB[i] = AllocRT(_newW, _newH, RenderTextureFormat.ARGB32, false);
//             _canvasRGB[i] = AllocRT(_dstW, _dstH, RenderTextureFormat.ARGB32, false);

//             _resizeDepth[i] = AllocRT(_newW, _newH, RenderTextureFormat.RFloat, false);
//             _canvasDepth[i] = AllocRT(_dstW, _dstH, RenderTextureFormat.RFloat, false);

//             _fullOutputRT[i] = AllocRT(_dstW, _dstH, RenderTextureFormat.RFloat, true);
//             _resultRT[i]     = AllocRT(_newW, _newH, RenderTextureFormat.RFloat, true);

//             _imgTensorGPU[i]    = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, RGB_CHANNELS,  _dstH, _dstW));
//             _promptTensorGPU[i] = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, DEPTH_CHANNELS, _dstH, _dstW));
//         }
//     }

//     private static RenderTexture AllocRT(int w, int h, RenderTextureFormat fmt, bool enableRW)
//     {
//         var rt = new RenderTexture(w, h, 0, fmt)
//         {
//             enableRandomWrite = enableRW,
//             wrapMode = TextureWrapMode.Clamp,
//             filterMode = FilterMode.Bilinear,
//             useMipMap = false,
//             autoGenerateMips = false
//         };
//         rt.Create();
//         return rt;
//     }

//     private static void ReleaseRTArray(RenderTexture[] arr)
//     {
//         if (arr == null) return;
//         foreach (var rt in arr)
//         {
//             if (rt == null) continue;
//             if (rt.IsCreated()) rt.Release();
//             UnityEngine.Object.Destroy(rt);
//         }
//     }

//     private static void DisposeTensorArray(Unity.InferenceEngine.Tensor<float>[] arr)
//     {
//         if (arr == null) return;
//         foreach (var t in arr) t?.Dispose();
//     }

//     private static void ComputeLetterboxParams(int srcW, int srcH, int dstW, int dstH, int multiple,
//         out int newW, out int newH, out int padX, out int padY)
//     {
//         float scale = Mathf.Min((float)dstW / srcW, (float)dstH / srcH);
//         newW = Mathf.Max(multiple, Mathf.FloorToInt(srcW * scale));
//         newH = Mathf.Max(multiple, Mathf.FloorToInt(srcH * scale));
//         newW -= newW % multiple;
//         newH -= newH % multiple;
//         padX = (dstW - newW) / 2;
//         padY = (dstH - newH) / 2;
//     }
// }
