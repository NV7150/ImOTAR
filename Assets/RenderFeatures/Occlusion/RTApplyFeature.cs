using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.ARFoundation;

namespace RenderPass {
    public class RTApplyFeature : ScriptableRendererFeature{
        [Serializable]
        public class MaterialSettings {
            [Header("Settings")]
            [SerializeField] private Material applyMaterial;
            [Header("Source RenderTexture")]
            [SerializeField] private RenderTexture sourceRT; // Expected R32_Float depth in meters
            [Header("Editor")]
            [SerializeField] private bool runOnEditor = true;

            public Material ApplyMaterial => applyMaterial;
            public RenderTexture SourceRT => sourceRT;
            public void SetSourceRT(RenderTexture rt){ sourceRT = rt; }
            public bool RunOnEditor => runOnEditor;
        }

        [SerializeField]private MaterialSettings settings;
        private RTApplyPass _mMaterialApplyPass;

        public override void Create() {
            _mMaterialApplyPass = new RTApplyPass(settings);
        }

        public void SetSourceTexture(RenderTexture rt){
            if(settings == null) return;
            settings.SetSourceRT(rt);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
#if UNITY_EDITOR
            if (settings.RunOnEditor == false)
            {
                return;
            }
#endif
            if (settings.ApplyMaterial == null)
            {
                return;
            }

            if (settings.SourceRT == null)
            {
                return;
            }

            // Ensure source RT is color-only (no depth/stencil) to satisfy RenderGraph import constraints
            var desc = settings.SourceRT.descriptor;
#if UNITY_6000_0_OR_NEWER
            bool hasDepth = desc.depthStencilFormat != GraphicsFormat.None;
#else
            bool hasDepth = desc.depthBufferBits != 0;
#endif
            if (hasDepth)
            {
                return;
            }

            // Fetch ARCameraBackground from current camera and pass it to the render pass
            var currentCamera = renderingData.cameraData.camera;
            var arBackground = currentCamera != null ? currentCamera.GetComponent<ARCameraBackground>() : null;
            if (arBackground != null)
            {
                _mMaterialApplyPass.SetARBackground(arBackground);
            }

            renderer.EnqueuePass(_mMaterialApplyPass);
        }
    }
}

