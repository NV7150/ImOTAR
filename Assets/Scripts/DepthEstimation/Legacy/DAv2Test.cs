// using System.Collections;

// using UnityEngine;

// public class DAv2Test : MonoBehaviour {
//     [SerializeField] private Unity.InferenceEngine.ModelAsset model;
//     [SerializeField] private Texture2D testImg;
//     [SerializeField] private RenderTexture visualizeTexture;

//     Unity.InferenceEngine.Model _runtimeModel;
//     Unity.InferenceEngine.Worker worker;
//     Unity.InferenceEngine.Tensor<float> _testImg;
//     Unity.InferenceEngine.Tensor<float> _output;

//     void Start() {
//         InitializeModel();
//         InitializeImg();
//         RunAndVis();
//     }

//     // Update is called once per frame
//     void Update() {
        
//     }

//     void RunAndVis(){
//         worker.Schedule(_testImg);
//         _output = worker.PeekOutput() as Unity.InferenceEngine.Tensor<float>;
//         Unity.InferenceEngine.TextureConverter.RenderToTexture(_output, visualizeTexture);
//     }

//     void InitializeModel(){
//         // _runtimeModel = ModelLoader.Load(model);
//         _runtimeModel = BuildRuntimeModel();
//         worker = new Unity.InferenceEngine.Worker(_runtimeModel, Unity.InferenceEngine.BackendType.GPUCompute);
//     }

//     void InitializeImg(){
//         _testImg = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, 3, 1024, 1024));
//         var transform = new TextureTransform().SetDimensions(1024, 1024);
//         Unity.InferenceEngine.TextureConverter.ToTensor(testImg, _testImg, transform);
//     }
    
//     private void OnDestroy() {
//         DisposeModel();
//         DisposeImgs();
//     }

//     void DisposeModel(){
//         worker.Dispose();
//     }

//     void DisposeImgs(){
//         _testImg.Dispose();
//         _output.Dispose();
//     }
//     Unity.InferenceEngine.Model BuildRuntimeModel() {
//         // 元モデル読込
//         var baseModel = Unity.InferenceEngine.ModelLoader.Load(model);

//         // Functional グラフ作成
//         var g      = new Unity.InferenceEngine.FunctionalGraph();
//         var inputs = g.AddInputs(baseModel);                 // 元モデルと同じ入力
//         var depth  = Unity.InferenceEngine.Functional.Forward(baseModel, inputs)[0];  // rank=3: (1, H, W)

//         // rank=3 → rank=4 : (1, 1, H, W)0
//         var depth4 = Unity.InferenceEngine.Functional.Unsqueeze(depth, 1);         // axis=1 で ch=1 を挿入 :contentReference[oaicite:0]{index=0}

//         // H・W 方向に min-max
//         var hw   = new[] { 2, 3 };
//         var dMin = Unity.InferenceEngine.Functional.ReduceMin(depth4, hw, true);   // keepdim=true :contentReference[oaicite:1]{index=1}
//         var dMax = Unity.InferenceEngine.Functional.ReduceMax(depth4, hw, true);

//         // 0-1 スケール
//         var norm = (depth4 - dMin) / ((dMax - dMin) + 1e-6f);

//         // 1ch → 3ch：Concat を ch 軸(1)で複製
//         var rgb  = Unity.InferenceEngine.Functional.Concat(new[] { norm, norm, norm }, 1); 

//         return g.Compile(rgb);   // rgb を最終出力
//     }
// }
