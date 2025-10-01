using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.ARFoundation;

namespace RenderPass {
    public class RTApplyPass : ScriptableRenderPass{
        private static readonly int DEPTH_TEX = Shader.PropertyToID("_DepthTex");
        private static readonly int UNITY_DISPLAY_TRANSFORM = Shader.PropertyToID("_UnityDisplayTransform");

        private readonly RTApplyFeature.MaterialSettings _settings;
        private Material _depthMaskMaterial;
        private ARCameraBackground _arBackground;

        private Matrix4x4 displayMatrix = Matrix4x4.identity;

        public RTApplyPass(RTApplyFeature.MaterialSettings settings) : base() {
            renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingOpaques + 1);
            _settings = settings;
        }

        private class DepthMaskPassData {
            public Material DepthMaskMaterial;
            public TextureHandle SourceTexture;
            public Matrix4x4 UnityDisplayTransform;
            public bool HasDisplayTransform;
        }
        public void SetARBackground(ARCameraBackground arBackground) {
            _arBackground = arBackground;
            
        }

        public void SetARCamera(ARCameraManager arCam){
            // arCam.frameReceived += OnCameraFrame;
        }

        // void OnCameraFrame(ARCameraFrameEventArgs args){
        //     if(!args.displayMatrix.HasValue)
        //         return;
        //     displayMatrix = args.displayMatrix.Value;
        // }
        

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

            // Resolve display transform from AR background (if available)
            Matrix4x4 disp = Matrix4x4.identity;
            bool hasDisp = false;
            if (_arBackground != null)
            {
                var mat = _arBackground.material;
                if (mat != null && mat.HasProperty(UNITY_DISPLAY_TRANSFORM))
                {
                    disp = mat.GetMatrix(UNITY_DISPLAY_TRANSFORM);
                    hasDisp = true;
                }
            }

            Debug.Log($"{disp}, {hasDisp}");

            // Import external RenderTexture (R32_Float expected)
            var imported = renderGraph.ImportTexture(RTHandles.Alloc(_settings.SourceRT));

            TextureHandle depthTextureToUse = imported;

            using (var builder = renderGraph.AddRasterRenderPass("RTApplyDepthMaskPass", out DepthMaskPassData passData)) {
                builder.UseTexture(depthTextureToUse, AccessFlags.Read);
                builder.SetRenderAttachment(cameraColorTextureHandle, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(cameraDepthTextureHandle, AccessFlags.Write);

                passData.DepthMaskMaterial = _depthMaskMaterial;
                passData.SourceTexture = depthTextureToUse;
                passData.UnityDisplayTransform = disp;
                passData.HasDisplayTransform = hasDisp;
                passData.DepthMaskMaterial.SetTexture(DEPTH_TEX, _settings.SourceRT);

                builder.SetRenderFunc((DepthMaskPassData data, RasterGraphContext context) => {
                    data.DepthMaskMaterial.SetTexture(DEPTH_TEX, data.SourceTexture);
                    if (data.HasDisplayTransform) {
                        data.DepthMaskMaterial.SetMatrix(UNITY_DISPLAY_TRANSFORM, data.UnityDisplayTransform);
                    }
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

