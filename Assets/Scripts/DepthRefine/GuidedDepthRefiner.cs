using System;
using UnityEngine;
using UnityEngine.Apple;

namespace ImOTAR.DepthRefine
{
    public enum GuideMode
    {
        Luminance = 0,
        RGB = 1,
        Edge = 2,
    }

    // Implements edge-preserving guided filtering for depth using a compute shader pipeline.
    // Fail-fast: validates all inputs and throws when misconfigured instead of silently continuing.
    public class GuidedDepthRefiner : DepthRefiner
    {
        [Header("Inputs")]
        [SerializeField] private RenderTexture guideTex; // RGB guide (any size, same aspect)
        [SerializeField] private RenderTexture edgeTex;  // Optional edge map (single channel)

    [Header("Compute")]
        [SerializeField] private ComputeShader shader;   // Assign Assets/Shaders/GuidedFilter.compute

        [Header("Parameters")]
        [SerializeField, Range(0, 64)] private int radius = 8;
        [SerializeField, Range(0f, 1f)] private float epsilon = 1e-3f;
        [SerializeField, Range(1, 8)] private int downsample = 1; // Fast guided; 1 disables
        [SerializeField, Range(0f, 1024f)] private float minValidWeight = 1f;
        [SerializeField] private float invalidDepthValue = -1f;
        [SerializeField] private GuideMode guideMode = GuideMode.Luminance;
        [SerializeField] private bool useFullRGBCovariance = false; // Placeholder for future extension

        // Luminance weights (linear RGB)
        [SerializeField] private Vector3 lumaWeights = new Vector3(0.299f, 0.587f, 0.114f);

    [Header("Output")]
    [SerializeField] private RenderTexture outputTex; // Must be RFloat, same size as input depth, random write enabled

    // Internal RTs
        private RenderTexture _rtGuide; // depth resolution RGB
        private RenderTexture _rtA0, _rtB0, _rtC0; // contributions at WxH
        private RenderTexture _rtA1, _rtB1, _rtC1; // integral images at (W+1)x(H+1)
        private RenderTexture _rtAB0; // m*a.xyz, m*b at WxH
        private RenderTexture _rtAB1; // integral at (W+1)x(H+1)

        // Kernel IDs
        private int kResampleGuide, kPrepareWeighted, kClearRGBA, kPrefixRowRGBA, kPrefixColRGBA;
        private int kBoxStats, kOutput;

        private struct Sizes { public int W, H, Wi, Hi; }

        private void OnEnable()
        {
            if (verboseLogs) Debug.Log("[GuidedDepthRefiner] OnEnable: caching kernels");
            CacheKernels();
        }

        private void OnDisable()
        {
            if (verboseLogs) Debug.Log("[GuidedDepthRefiner] OnDisable: releasing internal RTs");
            ReleaseAll();
        }

        private void CacheKernels()
        {
            if (shader == null) { if (verboseLogs) Debug.LogWarning("[GuidedDepthRefiner] CacheKernels: shader not assigned yet"); return; }
            kResampleGuide = shader.FindKernel("KResampleGuide");
            kPrepareWeighted = shader.FindKernel("KPrepareWeighted");
            kClearRGBA = shader.FindKernel("KClearRGBA");
            kPrefixRowRGBA = shader.FindKernel("KPrefixRowRGBA");
            kPrefixColRGBA = shader.FindKernel("KPrefixColRGBA");
            kBoxStats = shader.FindKernel("KBoxStats");
            kOutput = shader.FindKernel("KOutput");

            if (kResampleGuide < 0 || kPrepareWeighted < 0 || kClearRGBA < 0 ||
                kPrefixRowRGBA < 0 || kPrefixColRGBA < 0 || kBoxStats < 0 || kOutput < 0)
            {
                throw new InvalidOperationException("GuidedDepthRefiner: one or more compute kernels not found. Check GuidedFilter.compute.");
            }
        }

        public override RenderTexture Refine(RenderTexture depth)
        {
            var tStart = Time.realtimeSinceStartup;
            if (verboseLogs) Debug.Log("[GuidedDepthRefiner] Refine: begin");
            // Fail-fast validation
            if (shader == null) throw new InvalidOperationException("GuidedDepthRefiner: ComputeShader is not assigned.");
            if (depth == null) throw new ArgumentNullException(nameof(depth), "GuidedDepthRefiner: input depth is null.");
                // Input doesn't need random write; internal RTs do.
            if (depth.format != RenderTextureFormat.RFloat && depth.format != RenderTextureFormat.ARGBFloat)
                throw new InvalidOperationException($"GuidedDepthRefiner: input depth format must be RFloat/ARGBFloat (got {depth.format}).");
            if (guideTex == null && guideMode != GuideMode.Edge)
                throw new InvalidOperationException("GuidedDepthRefiner: guideTex is required for Luminance/RGB modes.");
            if (guideMode == GuideMode.Edge && edgeTex == null)
                throw new InvalidOperationException("GuidedDepthRefiner: edgeTex is required for Edge mode.");

            if (depth is RenderTexture drt && !drt.IsCreated())
                throw new InvalidOperationException("GuidedDepthRefiner: input depth RenderTexture is not created.");
            if (guideTex != null && !guideTex.IsCreated())
                throw new InvalidOperationException("GuidedDepthRefiner: guideTex is not created.");
            if (guideMode == GuideMode.Edge && edgeTex != null && !edgeTex.IsCreated())
                throw new InvalidOperationException("GuidedDepthRefiner: edgeTex is not created.");

            // Aspect ratio check (same ratio required)
            if (guideMode != GuideMode.Edge && guideTex != null)
            {
                float arDepth = (float)depth.width / depth.height;
                float arGuide = (float)guideTex.width / guideTex.height;
                if (Mathf.Abs(arDepth - arGuide) > 1e-3f)
                    throw new InvalidOperationException($"GuidedDepthRefiner: aspect ratio mismatch. depth {depth.width}x{depth.height}, guide {guideTex.width}x{guideTex.height}");
            }
            if (guideMode == GuideMode.Edge && edgeTex != null)
            {
                float arDepth = (float)depth.width / depth.height;
                float arEdge = (float)edgeTex.width / edgeTex.height;
                if (Mathf.Abs(arDepth - arEdge) > 1e-3f)
                    throw new InvalidOperationException($"GuidedDepthRefiner: aspect ratio mismatch. depth {depth.width}x{depth.height}, edge {edgeTex.width}x{edgeTex.height}");
            }

            // Sizes
            var sz = new Sizes { W = depth.width, H = depth.height, Wi = depth.width + 1, Hi = depth.height + 1 };

            // Ensure outputTex exists and is usable
            EnsureOutputTexture(sz.W, sz.H);

            if (verboseLogs)
            {
                Debug.Log($"[GuidedDepthRefiner] Params: mode={guideMode}, radius={radius}, eps={epsilon}, minW={minValidWeight}, invalid={invalidDepthValue}");
                int gW = guideTex != null ? guideTex.width : 0;
                int gH = guideTex != null ? guideTex.height : 0;
                int eW = edgeTex != null ? edgeTex.width : 0;
                int eH = edgeTex != null ? edgeTex.height : 0;
                Debug.Log($"[GuidedDepthRefiner] Sizes: depth={depth.width}x{depth.height}, guide={gW}x{gH}, edge={eW}x{eH}");
            }

            // Allocate RTs
            var tAlloc0 = Time.realtimeSinceStartup;
            EnsureAllRTs(sz);
            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] Alloc/Ensure RTs: {(Time.realtimeSinceStartup - tAlloc0)*1000f:F2} ms");

            // Common params
            shader.SetInt("_Width", sz.W);
            shader.SetInt("_Height", sz.H);
            shader.SetInt("_IntWidth", sz.Wi);
            shader.SetInt("_IntHeight", sz.Hi);
            shader.SetInt("_Radius", Mathf.Max(0, radius));
            shader.SetFloat("_Epsilon", Mathf.Max(0f, epsilon));
            shader.SetFloat("_MinValidWeight", Mathf.Max(0f, minValidWeight));
            shader.SetFloat("_InvalidDepth", invalidDepthValue);
            shader.SetVector("_LumaWeights", (Vector4)lumaWeights);

            // One-hot guide mode flags to keep shader branchless
            shader.SetFloat("_ModeLuma", guideMode == GuideMode.Luminance ? 1f : 0f);
            shader.SetFloat("_ModeRGB", guideMode == GuideMode.RGB ? 1f : 0f);
            shader.SetFloat("_ModeEdge", guideMode == GuideMode.Edge ? 1f : 0f);

            // 1) Resample guide to depth resolution
            int guideW = guideMode != GuideMode.Edge && guideTex != null ? guideTex.width : (edgeTex != null ? edgeTex.width : sz.W);
            int guideH = guideMode != GuideMode.Edge && guideTex != null ? guideTex.height : (edgeTex != null ? edgeTex.height : sz.H);
            shader.SetInt("_GuideSrcWidth", guideW);
            shader.SetInt("_GuideSrcHeight", guideH);
            shader.SetInt("_EdgeSrcWidth", edgeTex != null ? edgeTex.width : 0);
            shader.SetInt("_EdgeSrcHeight", edgeTex != null ? edgeTex.height : 0);
            shader.SetTexture(kResampleGuide, "_GuideDst", _rtGuide);
            shader.SetTexture(kResampleGuide, "_GuideSrc", guideTex);
            shader.SetTexture(kResampleGuide, "_EdgeSrc", edgeTex);
            var t0 = Time.realtimeSinceStartup;
            Dispatch2D(kResampleGuide, sz.W, sz.H);
            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] KResampleGuide: {(Time.realtimeSinceStartup - t0)*1000f:F2} ms");

            // 2) Prepare weighted contributions (m, mI, mP, mII, mIP)
            shader.SetTexture(kPrepareWeighted, "_Depth", depth);
            shader.SetTexture(kPrepareWeighted, "_Guide", _rtGuide);
            shader.SetTexture(kPrepareWeighted, "_A0", _rtA0);
            shader.SetTexture(kPrepareWeighted, "_B0", _rtB0);
            shader.SetTexture(kPrepareWeighted, "_C0", _rtC0);
            var t1 = Time.realtimeSinceStartup;
            Dispatch2D(kPrepareWeighted, sz.W, sz.H);
            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] KPrepareWeighted: {(Time.realtimeSinceStartup - t1)*1000f:F2} ms");

            // 3) Build integral images for A/B/C (clear then prefix row + col)
            var t2 = Time.realtimeSinceStartup;
            ClearIntegral(_rtA1, sz);
            ClearIntegral(_rtB1, sz);
            ClearIntegral(_rtC1, sz);
            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] Clear integrals: {(Time.realtimeSinceStartup - t2)*1000f:F2} ms");

            // Row prefix
            var t3 = Time.realtimeSinceStartup;
            PrefixRow(_rtA0, _rtA1, sz);
            PrefixRow(_rtB0, _rtB1, sz);
            PrefixRow(_rtC0, _rtC1, sz);
            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] PrefixRow A/B/C: {(Time.realtimeSinceStartup - t3)*1000f:F2} ms");

            // Column prefix (in-place on integral RTs)
            var t4 = Time.realtimeSinceStartup;
            PrefixCol(_rtA1, sz);
            PrefixCol(_rtB1, sz);
            PrefixCol(_rtC1, sz);
            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] PrefixCol A/B/C: {(Time.realtimeSinceStartup - t4)*1000f:F2} ms");

            // 4) Compute a,b (writes m*a.xyz, m*b to AB0)
            shader.SetTexture(kBoxStats, "_AInt", _rtA1);
            shader.SetTexture(kBoxStats, "_BInt", _rtB1);
            shader.SetTexture(kBoxStats, "_CInt", _rtC1);
            shader.SetTexture(kBoxStats, "_AB0", _rtAB0);
            var t5 = Time.realtimeSinceStartup;
            Dispatch2D(kBoxStats, sz.W, sz.H);
            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] KBoxStats: {(Time.realtimeSinceStartup - t5)*1000f:F2} ms");

            // 5) Integral of AB
            var t6 = Time.realtimeSinceStartup;
            ClearIntegral(_rtAB1, sz);
            PrefixRow(_rtAB0, _rtAB1, sz);
            PrefixCol(_rtAB1, sz);
            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] Integral of AB: {(Time.realtimeSinceStartup - t6)*1000f:F2} ms");

            // 6) Final output
            shader.SetTexture(kOutput, "_Guide", _rtGuide);
            shader.SetTexture(kOutput, "_AInt", _rtA1);
            shader.SetTexture(kOutput, "_ABInt", _rtAB1);
            shader.SetTexture(kOutput, "_OutTex", outputTex);
            var t7 = Time.realtimeSinceStartup;
            Dispatch2D(kOutput, sz.W, sz.H);
            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] KOutput: {(Time.realtimeSinceStartup - t7)*1000f:F2} ms");

            if (verboseLogs) Debug.Log($"[GuidedDepthRefiner] Refine: end, total {(Time.realtimeSinceStartup - tStart)*1000f:F2} ms");

            return outputTex;
        }

        private void EnsureOutputTexture(int w, int h)
        {
            // Replace if null or size/format mismatch
            bool needReplace = outputTex == null
                               || outputTex.width != w
                               || outputTex.height != h
                               || outputTex.format != RenderTextureFormat.RFloat;

            if (needReplace)
            {
                if (verboseLogs)
                {
                    int ow = outputTex != null ? outputTex.width : 0;
                    int oh = outputTex != null ? outputTex.height : 0;
                    Debug.Log($"[GuidedDepthRefiner] Allocating outputTex: {ow}x{oh} -> {w}x{h} RFloat");
                }
                if (outputTex != null)
                {
                    if (outputTex.IsCreated()) outputTex.Release();
                    UnityEngine.Object.Destroy(outputTex);
                }
                outputTex = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat)
                {
                    enableRandomWrite = true,
                    useMipMap = false,
                    autoGenerateMips = false,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point,
                    name = "GuidedFilter_Output"
                };
                if (!outputTex.Create())
                    throw new InvalidOperationException("GuidedDepthRefiner: failed to create outputTex.");
                return;
            }

            // Fixup randomWrite and creation if needed
            if (!outputTex.enableRandomWrite)
            {
                if (outputTex.IsCreated()) outputTex.Release();
                outputTex.enableRandomWrite = true;
            }
            if (!outputTex.IsCreated())
            {
                if (verboseLogs) Debug.Log("[GuidedDepthRefiner] Creating outputTex (was not created)");
                if (!outputTex.Create())
                    throw new InvalidOperationException("GuidedDepthRefiner: failed to Create() outputTex.");
            }
        }

        private void EnsureAllRTs(Sizes sz)
        {
            // Guide at depth resolution (RGB float)
            EnsureRT(ref _rtGuide, sz.W, sz.H, RenderTextureFormat.ARGBFloat, "GuidedFilter_Guide");

            // Contributions (WxH) RGBAFloat
            EnsureRT(ref _rtA0, sz.W, sz.H, RenderTextureFormat.ARGBFloat, "GuidedFilter_A0");
            EnsureRT(ref _rtB0, sz.W, sz.H, RenderTextureFormat.ARGBFloat, "GuidedFilter_B0");
            EnsureRT(ref _rtC0, sz.W, sz.H, RenderTextureFormat.ARGBFloat, "GuidedFilter_C0");

            // Integrals ((W+1)x(H+1)) RGBAFloat
            EnsureRT(ref _rtA1, sz.Wi, sz.Hi, RenderTextureFormat.ARGBFloat, "GuidedFilter_A1");
            EnsureRT(ref _rtB1, sz.Wi, sz.Hi, RenderTextureFormat.ARGBFloat, "GuidedFilter_B1");
            EnsureRT(ref _rtC1, sz.Wi, sz.Hi, RenderTextureFormat.ARGBFloat, "GuidedFilter_C1");

            // AB contributions and integral
            EnsureRT(ref _rtAB0, sz.W, sz.H, RenderTextureFormat.ARGBFloat, "GuidedFilter_AB0");
            EnsureRT(ref _rtAB1, sz.Wi, sz.Hi, RenderTextureFormat.ARGBFloat, "GuidedFilter_AB1");

            // Output is user-provided via outputTex; no internal allocation.
        }

        private static void EnsureRT(ref RenderTexture rt, int w, int h, RenderTextureFormat fmt, string name)
        {
            if (rt != null && (rt.width != w || rt.height != h || rt.format != fmt))
            {
                // Replace RT if size/format changed
                if (rt.IsCreated()) rt.Release();
                UnityEngine.Object.Destroy(rt);
                rt = null;
            }
            if (rt == null)
            {
                rt = new RenderTexture(w, h, 0, fmt)
                {
                    enableRandomWrite = true,
                    useMipMap = false,
                    autoGenerateMips = false,
                    name = name,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                if (!rt.Create()) throw new InvalidOperationException($"GuidedDepthRefiner: failed to create {name} ({w}x{h}, {fmt}).");
                // Name visible in Xcode frame captures
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Metal)
                {
                    // No API to set native label here, but keep Unity name informative
                }
            }
        }

        private void ClearIntegral(RenderTexture integralRT, Sizes sz)
        {
            shader.SetTexture(kClearRGBA, "_DstRGBA", integralRT);
            Dispatch2D(kClearRGBA, sz.Wi, sz.Hi);
        }

        private void PrefixRow(RenderTexture srcContrib, RenderTexture dstIntegral, Sizes sz)
        {
            shader.SetTexture(kPrefixRowRGBA, "_SrcRGBA", srcContrib);
            shader.SetTexture(kPrefixRowRGBA, "_DstRGBA", dstIntegral);
            Dispatch1D_Y(kPrefixRowRGBA, sz.Hi); // process rows y in [0..H]
        }

        private void PrefixCol(RenderTexture integralRT, Sizes sz)
        {
            shader.SetTexture(kPrefixColRGBA, "_DstRGBA", integralRT);
            Dispatch1D_X(kPrefixColRGBA, sz.Wi); // process columns x in [0..W]
        }

        private void Dispatch2D(int kernel, int w, int h)
        {
            const int TS = 16;
            int gx = (w + TS - 1) / TS;
            int gy = (h + TS - 1) / TS;
            shader.Dispatch(kernel, gx, gy, 1);
        }

        private void Dispatch1D_Y(int kernel, int rows)
        {
            // One thread per row
            shader.Dispatch(kernel, 1, rows, 1);
        }

        private void Dispatch1D_X(int kernel, int cols)
        {
            // One thread per column
            shader.Dispatch(kernel, cols, 1, 1);
        }

        private void ReleaseAll()
        {
            if (verboseLogs) Debug.Log("[GuidedDepthRefiner] Releasing internal RTs");
            Release(ref _rtGuide);
            Release(ref _rtA0); Release(ref _rtB0); Release(ref _rtC0);
            Release(ref _rtA1); Release(ref _rtB1); Release(ref _rtC1);
            Release(ref _rtAB0); Release(ref _rtAB1);
        }

        private static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            if (rt.IsCreated()) rt.Release();
            UnityEngine.Object.Destroy(rt);
            rt = null;
        }
    }
}
