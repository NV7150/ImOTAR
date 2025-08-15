using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;

namespace RenderPass {
    public class DPApplyPass : ScriptableRenderPass{
        private static readonly int DEPTH_TEX = Shader.PropertyToID("_DepthTex");
        private static readonly int TARGET_TEXTURE = Shader.PropertyToID("_TargetTexture");

        private readonly DPApplyFeature.MaterialSettings _settings;
        private DepthProvider _depthProvider;
        private int _lastTick = -1;
        private Texture2D _currentDepthTexture;
        private Material _depthMaskMaterial;

        public DPApplyPass(DPApplyFeature.MaterialSettings settings) : base() {
            // renderPassEvent = settings.RenderPassEvent;
            renderPassEvent =
                (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingOpaques + 1); // 301
            _settings = settings;
        }
        
        public void SetDepthProvider(DepthProvider depthProvider) {
            _depthProvider = depthProvider;
        }
        
        private class DepthMaskPassData {
            public Material DepthMaskMaterial;
            public TextureHandle applyTexture;
        }

        private class DepthTransformPassData {
            public TextureHandle RawDepth;
            public Material TransformMaterial;
        }
        
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraColorTextureHandle = resourceData.activeColorTexture;
            var cameraDepthTextureHandle = resourceData.activeDepthTexture;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        
            int w = cameraData.scaledWidth;
            int h = cameraData.scaledHeight;
            
            // DepthProviderの存在確認
            if (_depthProvider == null)
            {
                return;
            }
            
            // Tickを使った差分チェック
            int currentTick = _depthProvider.Tick;
            bool needsUpdate = _lastTick != currentTick;
            
            if (needsUpdate)
            {
                if(_depthProvider.DepthTex == null)
                {
                    Debug.Log("Cannot get new depth");
                    return;
                }

                // 古い深度テクスチャを削除
                if (_currentDepthTexture != null && _currentDepthTexture != _depthProvider.DepthTex)
                {
                    Object.DestroyImmediate(_currentDepthTexture);
                }

                // Debug.Log("Update");
                // 新しい深度テクスチャを取得
                _currentDepthTexture = _depthProvider.DepthTex;
                
                // マテリアルを初期化（初回のみ）
                if (_depthMaskMaterial == null)
                {
                    _depthMaskMaterial = new Material(_settings.ApplyMaterial);
                }
                
                _lastTick = currentTick;
                
            }

            // Step1: 小さなサイズで回転処理
            int texWidth = _currentDepthTexture.width;
            int texHeight = _currentDepthTexture.height;
            var rtHandle = renderGraph.ImportTexture(RTHandles.Alloc(_currentDepthTexture));
            TextureHandle depthTextureToUse;
            
            if (!_settings.DisableTransform)
            {
                var tempRT2 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, new RenderTextureDescriptor(w, h), "_TempRT2", true);
                
                using(var builder = renderGraph.AddRasterRenderPass("DepthRotatePass", out DepthTransformPassData passData)){
                    builder.UseTexture(rtHandle, AccessFlags.Read);
                    builder.SetRenderAttachment(tempRT2, 0, AccessFlags.Write);
                    passData.RawDepth = rtHandle; // 使わない
                    passData.TransformMaterial = _settings.TransformMaterial;
                    
                    builder.SetRenderFunc((DepthTransformPassData data, RasterGraphContext context) => {
                        data.TransformMaterial.SetFloat("_Angle", _settings.RotationAngle);
                        data.TransformMaterial.SetFloat("_TargetAspect", 1f);
                        Blitter.BlitTexture(context.cmd, data.RawDepth, Vector4.one, data.TransformMaterial, 0);
                    });
                }
                depthTextureToUse = tempRT2;
            }
            else
            {
                depthTextureToUse = rtHandle;
            }

            // _depthMaskMaterial.SetTexture(DEPTH_TEX, _currentDepthTexture);

            // 深度マスク書き込みパス
            using (var builder = renderGraph.AddRasterRenderPass("DepthMaskPass", out DepthMaskPassData passData)) {
                builder.UseTexture(depthTextureToUse, AccessFlags.Read);
                builder.SetRenderAttachment(cameraColorTextureHandle, 0, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);        // どうしても落とされたくない場合
                builder.SetRenderAttachmentDepth(cameraDepthTextureHandle, AccessFlags.Write);

                // パスデータを設定
                passData.DepthMaskMaterial = _depthMaskMaterial;
                passData.applyTexture = depthTextureToUse;
                passData.DepthMaskMaterial.SetTexture(DEPTH_TEX, _currentDepthTexture);
                
                // パス実行
                builder.SetRenderFunc((DepthMaskPassData data, RasterGraphContext context) => {
                    
                    passData.DepthMaskMaterial.SetTexture(DEPTH_TEX, data.applyTexture);
                    // マテリアルのテクスチャは既に設定済みなので、ここでは設定しない
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.DepthMaskMaterial, 0, MeshTopology.Triangles, 3);
                });
            }
        }
        
        public void Dispose()
        {
            // リソースのクリーンアップ
            if (_currentDepthTexture != null)
            {
                Object.DestroyImmediate(_currentDepthTexture);
                _currentDepthTexture = null;
            }
            
            if (_depthMaskMaterial != null)
            {
                Object.DestroyImmediate(_depthMaskMaterial);
                _depthMaskMaterial = null;
            }
        }
    }
} 