using System;
using UnityEngine;
using ImOTAR.Tools;

public class RegionMeanDepthRefiner : DepthRefiner {
    public enum ZeroRegionMode {
        MinusOne = 0,         // always output -1
        Zero = 1,             // always output 0
        TreatAsRegion = 2,    // aggregate like normal region
        PassthroughDepth = 3  // output original depth
    }

    [Header("Inputs")]
    [SerializeField] private RegionProvider regionProvider;
    [SerializeField] private RenderTexture depthTexture; // RFloat meters

    [Header("Compute")]
    [SerializeField] private ComputeShader shader;
    [SerializeField] private ZeroRegionMode zeroRegionMode = ZeroRegionMode.TreatAsRegion;

    [Header("Output")]
    [SerializeField] private RenderTexture output; // RFloat, region-sized

    [Header("Debug")] 
    [SerializeField] private bool debugLogStats = true; 
    [SerializeField, Range(1, 64)] private int debugLogCount = 8;

    private int kReset, kPass1, kPass2, kPass3, kPass4;

    private ComputeBuffer countBuf;      // uint[numRegions]
    private ComputeBuffer maxDepthMmBuf; // uint[numRegions]
    private ComputeBuffer sumScaledBuf;  // uint[numRegions]
    private ComputeBuffer scaleBuf;      // uint[numRegions]
    private int allocatedRegions = 0;

    private void OnEnable() {
        if (shader == null) throw new InvalidOperationException("[RegionMeanDepthRefiner] ComputeShader is not assigned.");
        kReset = shader.FindKernel("ResetBuffers");
        kPass1 = shader.FindKernel("Pass1_CountMax");
        kPass2 = shader.FindKernel("Pass2_ComputeScale");
        kPass3 = shader.FindKernel("Pass3_Sum");
        kPass4 = shader.FindKernel("Pass4_Write");
        if (kReset < 0 || kPass1 < 0 || kPass2 < 0 || kPass3 < 0 || kPass4 < 0)
            throw new InvalidOperationException("[RegionMeanDepthRefiner] Failed to find kernels in compute shader.");
    }

    private void OnDisable() {
        ReleaseBuffers();
    }

    public override RenderTexture Refine(RenderTexture depthRT) {
        // Allow explicit depthTexture override via field; else arg
        var dRT = depthTexture != null ? depthTexture : depthRT;
        if (dRT == null) throw new InvalidOperationException("[RegionMeanDepthRefiner] Depth RenderTexture is null.");
        if (dRT.format != RenderTextureFormat.RFloat) throw new InvalidOperationException($"[RegionMeanDepthRefiner] Depth RT must be RFloat. Got {dRT.format}.");
        if (!dRT.IsCreated()) throw new InvalidOperationException("[RegionMeanDepthRefiner] Depth RT is not created.");
        if (regionProvider == null) throw new InvalidOperationException("[RegionMeanDepthRefiner] RegionProvider is not assigned.");
        var rRT = regionProvider.CurrentRegion;
        if (rRT == null) throw new InvalidOperationException("[RegionMeanDepthRefiner] Region RT is null (from provider).");
        if (rRT.format != RenderTextureFormat.ARGB32) throw new InvalidOperationException($"[RegionMeanDepthRefiner] Region RT must be ARGB32. Got {rRT.format}.");
        if (!rRT.IsCreated()) throw new InvalidOperationException("[RegionMeanDepthRefiner] Region RT is not created.");

        int rw = rRT.width, rh = rRT.height;
        int dw = dRT.width, dh = dRT.height;
        if (rw <= 0 || rh <= 0 || dw <= 0 || dh <= 0) throw new InvalidOperationException("[RegionMeanDepthRefiner] Invalid texture dimensions.");
        
        EnsureOutput(rw, rh);

        int numRegions = Mathf.Max(1, regionProvider.CurrentRegionCount + 1); // include id==0
        EnsureBuffers(numRegions);

        // Bind common params
        shader.SetInts("_RegionSize", rw, rh);
        shader.SetInts("_DepthSize", dw, dh);
        shader.SetInt("_NumRegions", numRegions);
        shader.SetInt("_ZeroRegionMode", Mathf.Clamp((int)zeroRegionMode, 0, 3));

        // Bind SRVs/RW
        shader.SetTexture(kPass1, "_DepthTex", dRT);
        shader.SetTexture(kPass1, "_RegionTex", rRT);
        shader.SetTexture(kPass3, "_DepthTex", dRT);
        shader.SetTexture(kPass3, "_RegionTex", rRT);
        shader.SetTexture(kPass4, "_DepthTex", dRT);
        shader.SetTexture(kPass4, "_RegionTex", rRT);
        shader.SetTexture(kPass4, "_Output", output);

        shader.SetBuffer(kReset, "_Count", countBuf);
        shader.SetBuffer(kReset, "_MaxDepthMm", maxDepthMmBuf);
        shader.SetBuffer(kReset, "_SumScaled", sumScaledBuf);
        shader.SetBuffer(kReset, "_Scale", scaleBuf);

        shader.SetBuffer(kPass1, "_Count", countBuf);
        shader.SetBuffer(kPass1, "_MaxDepthMm", maxDepthMmBuf);

        shader.SetBuffer(kPass2, "_Count", countBuf);
        shader.SetBuffer(kPass2, "_MaxDepthMm", maxDepthMmBuf);
        shader.SetBuffer(kPass2, "_Scale", scaleBuf);

        shader.SetBuffer(kPass3, "_Scale", scaleBuf);
        shader.SetBuffer(kPass3, "_SumScaled", sumScaledBuf);

        shader.SetBuffer(kPass4, "_Count", countBuf);
        shader.SetBuffer(kPass4, "_Scale", scaleBuf);
        shader.SetBuffer(kPass4, "_SumScaled", sumScaledBuf);

        // Dispatch
        int gx = (rw + 15) / 16;
        int gy = (rh + 15) / 16;

        // Reset
        int rBlocks = (numRegions + 255) / 256;
        shader.Dispatch(kReset, rBlocks, 1, 1);

        // Pass1: count/max
        shader.Dispatch(kPass1, gx, gy, 1);

        // Pass2: scale per region
        shader.Dispatch(kPass2, rBlocks, 1, 1);

        // Pass3: sum scaled
        shader.Dispatch(kPass3, gx, gy, 1);

        // Pass4: write output
        shader.Dispatch(kPass4, gx, gy, 1);

        if (debugLogStats) {
            int n = Mathf.Min(numRegions, Mathf.Max(1, debugLogCount));
            uint[] dbgCount = new uint[n];
            uint[] dbgScale = new uint[n];
            uint[] dbgSum = new uint[n];
            uint[] dbgMaxMm = new uint[n];
            countBuf.GetData(dbgCount, 0, 0, n);
            scaleBuf.GetData(dbgScale, 0, 0, n);
            sumScaledBuf.GetData(dbgSum, 0, 0, n);
            maxDepthMmBuf.GetData(dbgMaxMm, 0, 0, n);
            for (int i = 0; i < n; i++) {
                double mean = -1.0;
                if (dbgCount[i] > 0 && dbgScale[i] > 0) {
                    mean = (double)dbgSum[i] / ((double)dbgCount[i] * (double)dbgScale[i]);
                }
                Debug.Log($"[RegionMeanDepthRefiner] id={i} cnt={dbgCount[i]} scale={dbgScale[i]} maxMm={dbgMaxMm[i]} sum={dbgSum[i]} mean={mean:F6}");
            }
        }

        return output;
    }

    private void EnsureOutput(int w, int h) {
        if (output != null && (output.width != w || output.height != h || output.format != RenderTextureFormat.RFloat)) {
            output.Release();
            DestroyImmediate(output);
            output = null;
        }
        if (output == null) {
            output = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            output.enableRandomWrite = true;
            output.useMipMap = false;
            output.wrapMode = TextureWrapMode.Clamp;
            output.filterMode = FilterMode.Point;
            if (!output.Create()) throw new InvalidOperationException("[RegionMeanDepthRefiner] Failed to create output RenderTexture.");
        }
        if (!output.enableRandomWrite) throw new InvalidOperationException("[RegionMeanDepthRefiner] Output must have enableRandomWrite=true.");
    }

    private void EnsureBuffers(int numRegions) {
        if (numRegions <= 0) throw new InvalidOperationException("[RegionMeanDepthRefiner] numRegions must be > 0.");
        if (numRegions <= allocatedRegions && countBuf != null && maxDepthMmBuf != null && sumScaledBuf != null && scaleBuf != null) return;

        ReleaseBuffers();

        countBuf = new ComputeBuffer(numRegions, sizeof(uint));
        maxDepthMmBuf = new ComputeBuffer(numRegions, sizeof(uint));
        sumScaledBuf = new ComputeBuffer(numRegions, sizeof(uint));
        scaleBuf = new ComputeBuffer(numRegions, sizeof(uint));
        allocatedRegions = numRegions;
    }

    private void ReleaseBuffers() {
        countBuf?.Dispose(); countBuf = null;
        maxDepthMmBuf?.Dispose(); maxDepthMmBuf = null;
        sumScaledBuf?.Dispose(); sumScaledBuf = null;
        scaleBuf?.Dispose(); scaleBuf = null;
        allocatedRegions = 0;
    }
}


