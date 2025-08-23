using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

namespace RenderPass {
    public class RTApplyPass : ScriptableRenderPass{
        private static readonly int DEPTH_TEX = Shader.PropertyToID("_DepthTex");

        private readonly RTApplyFeature.MaterialSettings _settings;
        private Material _depthMaskMaterial;

        public RTApplyPass(RTApplyFeature.MaterialSettings settings) : base() {
            renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingOpaques + 1);
            _settings = settings;
        }

        private class DepthMaskPassData {
            public Material DepthMaskMaterial;
            public TextureHandle SourceTexture;
        }

        

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraColorTextureHandle = resourceData.activeColorTexture;
            var cameraDepthTextureHandle = resourceData.activeDepthTexture;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            int w = cameraData.scaledWidth;
            int h = cameraData.scaledHeight;


            if (_settings.SourceRT == null)
            {
                return;
            }

            // Validate source is color-only (no depth/stencil)
            var srcDesc = _settings.SourceRT.descriptor;
#if UNITY_6000_0_OR_NEWER
            bool hasDepth = srcDesc.depthStencilFormat != GraphicsFormat.None;
#else
            bool hasDepth = srcDesc.depthBufferBits != 0;
#endif
            if (hasDepth)
            {
                return;
            }

            if (_depthMaskMaterial == null)
            {
                _depthMaskMaterial = new Material(_settings.ApplyMaterial);
            }

            // Import external RenderTexture (R32_Float expected)
            Debug.Log($"[RTApplyPass] ImportTexture({_settings.SourceRT.name})");
            var imported = renderGraph.ImportTexture(RTHandles.Alloc(_settings.SourceRT));

            TextureHandle depthTextureToUse = imported;

            using (var builder = renderGraph.AddRasterRenderPass("RTApplyDepthMaskPass", out DepthMaskPassData passData)) {
                builder.UseTexture(depthTextureToUse, AccessFlags.Read);
                builder.SetRenderAttachment(cameraColorTextureHandle, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(cameraDepthTextureHandle, AccessFlags.Write);

                passData.DepthMaskMaterial = _depthMaskMaterial;
                passData.SourceTexture = depthTextureToUse;
                passData.DepthMaskMaterial.SetTexture(DEPTH_TEX, _settings.SourceRT);

                builder.SetRenderFunc((DepthMaskPassData data, RasterGraphContext context) => {
                    data.DepthMaskMaterial.SetTexture(DEPTH_TEX, data.SourceTexture);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.DepthMaskMaterial, 0, MeshTopology.Triangles, 3);
                });
            }
        }

        public void Dispose(){
            if (_depthMaskMaterial != null)
            {
                Object.DestroyImmediate(_depthMaskMaterial);
                _depthMaskMaterial = null;
            }
        }
    }
}

