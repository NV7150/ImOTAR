using System;
using UnityEngine;

public class GradRefiner : DepthRefiner {
    public enum ZeroRegionMode {
        MinusOne = 0,
        Zero = 1,
        TreatAsRegion = 2,
        PassthroughDepth = 3
    }

    public enum CoreDepthMode {
        Mean = 0,
        Median = 1
    }

    public enum InsufficientMode {
        MinusOne = 0,
        FillCore = 1
    }

    [Header("Inputs")]
    [SerializeField] private RegionProvider regionProvider;
    [SerializeField] private RenderTexture depthTexture; // RFloat meters

    [Header("Compute")]
    [SerializeField] private ComputeShader shader; // RegionGradDepth.compute
    [SerializeField] private ZeroRegionMode zeroRegionMode = ZeroRegionMode.TreatAsRegion;
    [SerializeField] private CoreDepthMode coreDepthMode = CoreDepthMode.Mean;
    [SerializeField] private int histogramBins = 256;

    [Header("Gradient Sampling (Ring)")]
    [SerializeField, Range(0f, 1f)] private float ringRin = 0.25f;
    [SerializeField, Range(0f, 1f)] private float ringRout = 0.75f;
    [SerializeField] private uint minHalfSamples = 16;
    [SerializeField] private InsufficientMode insufficientMode = InsufficientMode.MinusOne;

    [Header("Temporal EMA")]
    [SerializeField] private bool useEMA = true;
    [SerializeField, Range(0f, 1f)] private float alphaInner = 0.3f;
    [SerializeField, Range(0f, 1f)] private float alphaBoundary = 0.7f;

    [Header("Output")]
    [SerializeField] private RenderTexture output; // RFloat, region-sized

    [Header("Debug")] 
    [SerializeField] private bool debugLogStats = false; 
    [SerializeField, Range(1, 64)] private int debugLogCount = 8;

    // Kernels
    private int kResetStats, kResetGeom, kClearHist, kPass1, kPass2, kPass3Mean, kPass3Median, kPassH, kPass4;

    // Buffers (packed)
    private ComputeBuffer statsBuf;      // RegionStats[numRegions]
    private ComputeBuffer halvesBuf;     // RegionHalves[numRegions]
    private ComputeBuffer centerBuf;     // float4[numRegions]
    private ComputeBuffer scaleCoreBuf;  // uint2[numRegions]
    private ComputeBuffer histBuf;       // uint[numRegions * bins]

    private int allocatedRegions = 0;
    private int allocatedBins = 0;

    private RenderTexture prevOutput; // RFloat, region-sized (read-only in compute)
    private bool prevValid = false;

    // CPU mirror structs for debug reads (must match compute layout)
    private struct RegionStatsCPU {
        public uint count;
        public uint maxDepthMm;
        public uint minDepthMm;
        public uint sumScaledAll;
        public uint sumX;
        public uint sumY;
        public uint minX;
        public uint maxX;
        public uint minY;
        public uint maxY;
    }

    private struct UInt2CPU { public uint x; public uint y; }

    private void OnEnable() {
        if (shader == null) throw new InvalidOperationException("[GradRefiner] ComputeShader is not assigned.");
        kResetStats  = shader.FindKernel("ResetStats");
        kResetGeom   = shader.FindKernel("ResetGeom");
        kClearHist   = shader.FindKernel("ClearHist");
        kPass1       = shader.FindKernel("Pass1_Stats");
        kPass2       = shader.FindKernel("Pass2_ScaleCenter");
        kPass3Mean   = shader.FindKernel("Pass3_AccumulateMean");
        kPass3Median = shader.FindKernel("Pass3_AccumulateMedian");
        kPassH       = shader.FindKernel("PassH_Median");
        kPass4       = shader.FindKernel("Pass4_Write");
        if (kResetStats < 0 || kResetGeom < 0 || kPass1 < 0 || kPass2 < 0 || kPass3Mean < 0 || kPass4 < 0)
            throw new InvalidOperationException("[GradRefiner] Failed to find kernels in compute shader.");
    }

    private void OnDisable() {
        ReleaseBuffers();
    }

    public override RenderTexture Refine(RenderTexture depthRT) {
        var dRT = depthTexture != null ? depthTexture : depthRT;
        if (dRT == null) throw new InvalidOperationException("[GradRefiner] Depth RenderTexture is null.");
        if (dRT.format != RenderTextureFormat.RFloat) throw new InvalidOperationException($"[GradRefiner] Depth RT must be RFloat. Got {dRT.format}.");
        if (!dRT.IsCreated()) throw new InvalidOperationException("[GradRefiner] Depth RT is not created.");
        if (regionProvider == null) throw new InvalidOperationException("[GradRefiner] RegionProvider is not assigned.");
        var rRT = regionProvider.CurrentRegion;
        if (rRT == null) throw new InvalidOperationException("[GradRefiner] Region RT is null (from provider).");
        if (rRT.format != RenderTextureFormat.ARGB32) throw new InvalidOperationException($"[GradRefiner] Region RT must be ARGB32. Got {rRT.format}.");
        if (!rRT.IsCreated()) throw new InvalidOperationException("[GradRefiner] Region RT is not created.");

        int rw = rRT.width, rh = rRT.height;
        int dw = dRT.width, dh = dRT.height;
        if (rw <= 0 || rh <= 0 || dw <= 0 || dh <= 0) throw new InvalidOperationException("[GradRefiner] Invalid texture dimensions.");

        EnsureOutput(rw, rh);
        EnsurePrevOutput(rw, rh);

        int numRegions = Mathf.Max(1, regionProvider.CurrentRegionCount + 1); // include id==0
        EnsureBuffers(numRegions, Mathf.Max(1, histogramBins));

        // Params
        shader.SetInts("_RegionSize", rw, rh);
        shader.SetInts("_DepthSize", dw, dh);
        shader.SetInt("_NumRegions", numRegions);
        shader.SetInt("_ZeroRegionMode", Mathf.Clamp((int)zeroRegionMode, 0, 3));
        shader.SetInt("_UseEMA", useEMA ? 1 : 0);
        shader.SetInt("_PrevValid", prevValid ? 1 : 0);
        shader.SetFloat("_AlphaInner", Mathf.Clamp01(alphaInner));
        shader.SetFloat("_AlphaBoundary", Mathf.Clamp01(alphaBoundary));

        shader.SetInt("_CoreDepthMode", Mathf.Clamp((int)coreDepthMode, 0, 1));
        shader.SetInt("_Bins", Mathf.Max(1, histogramBins));
        shader.SetFloat("_RingRin", Mathf.Clamp01(ringRin));
        shader.SetFloat("_RingRout", Mathf.Clamp01(ringRout));
        shader.SetInt("_MinHalfSamples", (int)Mathf.Max(0, (int)minHalfSamples));
        shader.SetInt("_InsufficientMode", Mathf.Clamp((int)insufficientMode, 0, 1));

        // Textures
        shader.SetTexture(kPass1, "_DepthTex", dRT);
        shader.SetTexture(kPass1, "_RegionTex", rRT);
        shader.SetTexture(kPass3Mean, "_DepthTex", dRT);
        shader.SetTexture(kPass3Mean, "_RegionTex", rRT);
        shader.SetTexture(kPass3Median, "_DepthTex", dRT);
        shader.SetTexture(kPass3Median, "_RegionTex", rRT);
        shader.SetTexture(kPass4, "_DepthTex", dRT);
        shader.SetTexture(kPass4, "_RegionTex", rRT);
        if (prevOutput != null) shader.SetTexture(kPass4, "_PrevOutput", prevOutput);
        shader.SetTexture(kPass4, "_Output", output);

        // Buffers
        // Bind packed buffers for resets
        shader.SetBuffer(kResetStats, "_Stats", statsBuf);
        shader.SetBuffer(kResetGeom,  "_Halves", halvesBuf);
        shader.SetBuffer(kResetGeom,  "_Center", centerBuf);
        shader.SetBuffer(kResetGeom,  "_ScaleCore", scaleCoreBuf);
        if (histBuf != null) shader.SetBuffer(kClearHist, "_Hist", histBuf);

        shader.SetBuffer(kPass1, "_Stats", statsBuf);

        shader.SetBuffer(kPass2, "_Stats", statsBuf);
        shader.SetBuffer(kPass2, "_ScaleCore", scaleCoreBuf);
        shader.SetBuffer(kPass2, "_Center", centerBuf);

        shader.SetBuffer(kPass3Mean,   "_ScaleCore", scaleCoreBuf);
        shader.SetBuffer(kPass3Mean,   "_Stats", statsBuf);
        shader.SetBuffer(kPass3Mean,   "_Halves", halvesBuf);
        shader.SetBuffer(kPass3Mean,   "_Center", centerBuf);

        shader.SetBuffer(kPass3Median, "_ScaleCore", scaleCoreBuf);
        shader.SetBuffer(kPass3Median, "_Stats", statsBuf);
        shader.SetBuffer(kPass3Median, "_Halves", halvesBuf);
        shader.SetBuffer(kPass3Median, "_Center", centerBuf);
        if (histBuf != null) shader.SetBuffer(kPass3Median, "_Hist", histBuf);

        shader.SetBuffer(kPassH, "_Stats", statsBuf);
        if (histBuf != null) shader.SetBuffer(kPassH, "_Hist", histBuf);
        shader.SetBuffer(kPassH, "_ScaleCore", scaleCoreBuf);

        shader.SetBuffer(kPass4, "_Stats", statsBuf);
        shader.SetBuffer(kPass4, "_ScaleCore", scaleCoreBuf);
        shader.SetBuffer(kPass4, "_Halves", halvesBuf);
        shader.SetBuffer(kPass4, "_Center", centerBuf);

        // Dispatch sizes
        int gx = (rw + 15) / 16;
        int gy = (rh + 15) / 16;
        int rBlocks = (numRegions + 255) / 256;

        shader.Dispatch(kResetStats, rBlocks, 1, 1);
        shader.Dispatch(kResetGeom, rBlocks, 1, 1);
        shader.Dispatch(kPass1, gx, gy, 1);
        shader.Dispatch(kPass2, rBlocks, 1, 1);
        if (coreDepthMode == CoreDepthMode.Median && histBuf != null) {
            shader.Dispatch(kClearHist, (numRegions * Mathf.Max(1, histogramBins) + 255) / 256, 1, 1);
            shader.Dispatch(kPass3Median, gx, gy, 1);
            shader.Dispatch(kPassH, rBlocks, 1, 1);
        } else {
            shader.Dispatch(kPass3Mean, gx, gy, 1);
        }
        shader.Dispatch(kPass4, gx, gy, 1);

        if (useEMA && output != null && prevOutput != null) {
            Graphics.Blit(output, prevOutput);
            prevValid = true;
        } else {
            prevValid = false;
        }

        if (debugLogStats) {
            int n = Mathf.Min(numRegions, Mathf.Max(1, debugLogCount));
            var stats = new RegionStatsCPU[n];
            var scArr = new UInt2CPU[n];
            statsBuf.GetData(stats, 0, 0, n);
            scaleCoreBuf.GetData(scArr, 0, 0, n);
            for (int i = 0; i < n; i++) {
                double mean = -1.0;
                if (stats[i].count > 0 && scArr[i].x > 0) {
                    mean = (double)stats[i].sumScaledAll / ((double)stats[i].count * (double)scArr[i].x);
                }
                Debug.Log($"[GradRefiner] id={i} cnt={stats[i].count} scale={scArr[i].x} mean={mean:F6}");
            }
        }

        return output;
    }

    private void EnsureOutput(int w, int h) {
        if (output != null && (output.width != w || output.height != h || output.format != RenderTextureFormat.RFloat)) {
            output.Release();
            UnityEngine.Object.DestroyImmediate(output);
            output = null;
            prevValid = false;
        }
        if (output == null) {
            output = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            output.enableRandomWrite = true;
            output.useMipMap = false;
            output.wrapMode = TextureWrapMode.Clamp;
            output.filterMode = FilterMode.Point;
            if (!output.Create()) throw new InvalidOperationException("[GradRefiner] Failed to create output RenderTexture.");
        }
        if (!output.enableRandomWrite) throw new InvalidOperationException("[GradRefiner] Output must have enableRandomWrite=true.");
    }

    private void EnsurePrevOutput(int w, int h) {
        if (prevOutput != null && (prevOutput.width != w || prevOutput.height != h || prevOutput.format != RenderTextureFormat.RFloat)) {
            prevOutput.Release();
            UnityEngine.Object.DestroyImmediate(prevOutput);
            prevOutput = null;
            prevValid = false;
        }
        if (prevOutput == null) {
            prevOutput = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            prevOutput.enableRandomWrite = false;
            prevOutput.useMipMap = false;
            prevOutput.wrapMode = TextureWrapMode.Clamp;
            prevOutput.filterMode = FilterMode.Point;
            if (!prevOutput.Create()) throw new InvalidOperationException("[GradRefiner] Failed to create prev RenderTexture.");
            prevValid = false;
        }
    }

    private void EnsureBuffers(int numRegions, int bins) {
        if (numRegions <= 0) throw new InvalidOperationException("[GradRefiner] numRegions must be > 0.");
        bool needRealloc = (numRegions > allocatedRegions) || (bins != allocatedBins);
        if (!needRealloc && statsBuf != null && halvesBuf != null && centerBuf != null && scaleCoreBuf != null && (coreDepthMode == CoreDepthMode.Median ? histBuf != null : true)) return;

        ReleaseBuffers();

        int n = numRegions;
        int statsStride = sizeof(uint) * 10;
        int halvesStride = sizeof(uint) * 8;
        int centerStride = sizeof(float) * 4;
        int scaleCoreStride = sizeof(uint) * 2;
        statsBuf = new ComputeBuffer(n, statsStride);
        halvesBuf = new ComputeBuffer(n, halvesStride);
        centerBuf = new ComputeBuffer(n, centerStride);
        scaleCoreBuf = new ComputeBuffer(n, scaleCoreStride);
        if (coreDepthMode == CoreDepthMode.Median) histBuf = new ComputeBuffer(n * bins, sizeof(uint));

        allocatedRegions = numRegions;
        allocatedBins = bins;
    }

    private void ReleaseBuffers() {
        statsBuf?.Dispose(); statsBuf = null;
        halvesBuf?.Dispose(); halvesBuf = null;
        centerBuf?.Dispose(); centerBuf = null;
        scaleCoreBuf?.Dispose(); scaleCoreBuf = null;
        histBuf?.Dispose(); histBuf = null;

        allocatedRegions = 0;
        allocatedBins = 0;
    }

    private void OnDestroy() {
        if (prevOutput != null) { prevOutput.Release(); UnityEngine.Object.DestroyImmediate(prevOutput); prevOutput = null; }
    }
}


