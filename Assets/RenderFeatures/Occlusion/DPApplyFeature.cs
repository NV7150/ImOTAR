using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace RenderPass {
    public class DPApplyFeature : ScriptableRendererFeature{
        [Serializable]
        public class MaterialSettings {
            [Header("Settings")]
            [SerializeField] private Material applyMaterial;
            [SerializeField] private Material transformMaterial;
            [SerializeField][Range(0, 360)] private float rotationAngle;
            [SerializeField] private bool disableTransform;

            public Material ApplyMaterial => applyMaterial;
            public Material TransformMaterial => transformMaterial;

            public float RotationAngle => rotationAngle;
            public bool DisableTransform => disableTransform;
        }

        [SerializeField]private MaterialSettings settings;
        private DPApplyPass _mMaterialApplyPass;
        private CpuFrameProvider _cachedDepthProvider;

        public override void Create() {
            _mMaterialApplyPass = new DPApplyPass(settings);
            
            // シーン内からDepthProviderを一回だけ検索
            _cachedDepthProvider = FindFirstObjectByType<CpuFrameProvider>();
            if (_cachedDepthProvider != null) {
                _mMaterialApplyPass.SetDepthProvider(_cachedDepthProvider);
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
// #if UNITY_EDITOR
//             // Unity Editorでは無効にする
//             return;
// #endif

            if (settings.ApplyMaterial == null || settings.TransformMaterial == null)
            {
                Debug.LogWarning("DPApplyFeature: Material is not assigned");
                return;
            }
            
            if (_cachedDepthProvider == null)
            {
                #if UNITY_EDITOR
                #else
                Debug.LogWarning("DPApplyFeature: DepthProvider not found in scene");
                _cachedDepthProvider = FindFirstObjectByType<DepthProvider>();
                if (_cachedDepthProvider != null) {
                    _mMaterialApplyPass.SetDepthProvider(_cachedDepthProvider);
                }
                #endif
                return;
            }
            
            renderer.EnqueuePass(_mMaterialApplyPass);
        }
    }
} 