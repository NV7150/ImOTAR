using System;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class RadiusNeighborFilter : SplatManager {

    [Header("Input")]
    [SerializeField] private SplatManager input;

    [Header("Compute")]
    [SerializeField] private ComputeShader filter;

    [Header("Radius Filter (meters)")]
    [SerializeField] private float neighborRadius = 0.02f; // physical meters
    [SerializeField] private int minNeighbors = 3;         // inclusive lower bound
    [SerializeField] private int maxNeighbors = -1;        // -1 disables upper bound

    [Header("Hash Grid")]
    [SerializeField] private int hashSizePow2 = 1 << 18;   // power of two (e.g., 262144)
    [SerializeField] private Vector3 gridOrigin = Vector3.zero;

    [Header("Debug")] 
    [SerializeField] private bool verbose = false;
    [SerializeField] private string logPrefix = "[RadiusFilter]";

    private int kInit, kBuild, kCount, kCompact;

    // Shader property IDs
    private int pPointsIn, pPointsOut, pCellHead, pNextIndex, pMask, pCounter;
    private int pCount, pHashSize, pHashMask, pGridOrigin, pCellSize, pRadius, pRadius2, pMinN, pMaxN;

    private const int THREAD_GROUP_SIZE = 256;

    private void OnEnable(){
        if (input == null) throw new NullReferenceException("RadiusNeighborFilter: input not assigned");
        if (filter == null) throw new NullReferenceException("RadiusNeighborFilter: filter compute not assigned");
        if (hashSizePow2 <= 0 || (hashSizePow2 & (hashSizePow2 - 1)) != 0) throw new ArgumentException("RadiusNeighborFilter: hashSizePow2 must be power of two");
        if (neighborRadius <= 0) throw new ArgumentOutOfRangeException(nameof(neighborRadius), "RadiusNeighborFilter: neighborRadius must be > 0");
        if (minNeighbors < 0) throw new ArgumentOutOfRangeException(nameof(minNeighbors), "RadiusNeighborFilter: minNeighbors must be >= 0");

        kInit    = filter.FindKernel("KInitHeads");
        kBuild   = filter.FindKernel("KBuildLists");
        kCount   = filter.FindKernel("KCountNeighbors");
        kCompact = filter.FindKernel("KCompact");

        pPointsIn   = Shader.PropertyToID("_PointsIn");
        pPointsOut  = Shader.PropertyToID("_PointsOut");
        pCellHead   = Shader.PropertyToID("_CellHead");
        pNextIndex  = Shader.PropertyToID("_NextIndex");
        pMask       = Shader.PropertyToID("_Mask");
        pCounter    = Shader.PropertyToID("_Counter");
        pCount      = Shader.PropertyToID("_Count");
        pHashSize   = Shader.PropertyToID("_HashSize");
        pHashMask   = Shader.PropertyToID("_HashMask");
        pGridOrigin = Shader.PropertyToID("_GridOrigin");
        pCellSize   = Shader.PropertyToID("_CellSize");
        pRadius     = Shader.PropertyToID("_Radius");
        pRadius2    = Shader.PropertyToID("_Radius2");
        pMinN       = Shader.PropertyToID("_MinNeighbors");
        pMaxN       = Shader.PropertyToID("_MaxNeighbors");

        input.OnSplatReady += OnInputSplat;
    }

    private void OnDisable(){
        if (input != null){
            input.OnSplatReady -= OnInputSplat;
        }
    }

    private void OnInputSplat(Splat inSplat){
        if (inSplat == null) throw new ArgumentNullException(nameof(inSplat));
        if (!inSplat.PointsBuffer.IsValid()) throw new InvalidOperationException("RadiusNeighborFilter: input buffer invalid");
        int n = inSplat.Count;
        if (n <= 0) return; // nothing to do

        // Working buffers
        var cellHead  = new GraphicsBuffer(GraphicsBuffer.Target.Structured, hashSizePow2, sizeof(int));
        var nextIndex = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, sizeof(int));
        var maskBuf   = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, sizeof(uint));
        var counter   = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
        var pointsOut = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, sizeof(float) * 4);

        try {
            // Reset counter to 0
            counter.SetData(new uint[]{0u});

            // Common uniforms
            float cellSize = neighborRadius; // grid cell size equals radius
            int hashMask = hashSizePow2 - 1;
            float radius2 = neighborRadius * neighborRadius;

            // Prepare KInitHeads
            filter.SetBuffer(kInit, pCellHead, cellHead);
            filter.SetInt(pHashSize, hashSizePow2);

            // Dispatch KInitHeads
            int groupsInit = (hashSizePow2 + THREAD_GROUP_SIZE - 1) / THREAD_GROUP_SIZE;
            filter.Dispatch(kInit, groupsInit, 1, 1);

            // Build lists
            filter.SetInt(pCount, n);
            filter.SetInt(pHashSize, hashSizePow2);
            filter.SetInt(pHashMask, hashMask);
            filter.SetVector(pGridOrigin, gridOrigin);
            filter.SetFloat(pCellSize, cellSize);
            filter.SetBuffer(kBuild, pPointsIn, inSplat.PointsBuffer);
            filter.SetBuffer(kBuild, pCellHead, cellHead);
            filter.SetBuffer(kBuild, pNextIndex, nextIndex);

            int groupsN = (n + THREAD_GROUP_SIZE - 1) / THREAD_GROUP_SIZE;
            filter.Dispatch(kBuild, groupsN, 1, 1);

            // Count neighbors
            int maxN = (maxNeighbors < 0) ? int.MaxValue : maxNeighbors;
            filter.SetFloat(pRadius, neighborRadius);
            filter.SetFloat(pRadius2, radius2);
            filter.SetInt(pMinN, minNeighbors);
            filter.SetInt(pMaxN, maxN);

            filter.SetBuffer(kCount, pPointsIn, inSplat.PointsBuffer);
            filter.SetBuffer(kCount, pCellHead, cellHead);
            filter.SetBuffer(kCount, pNextIndex, nextIndex);
            filter.SetBuffer(kCount, pMask, maskBuf);
            filter.Dispatch(kCount, groupsN, 1, 1);

            // Compact
            filter.SetBuffer(kCompact, pPointsIn, inSplat.PointsBuffer);
            filter.SetBuffer(kCompact, pPointsOut, pointsOut);
            filter.SetBuffer(kCompact, pMask, maskBuf);
            filter.SetBuffer(kCompact, pCounter, counter);
            filter.Dispatch(kCompact, groupsN, 1, 1);

            // Read final count
            uint[] counterData = new uint[1];
            counter.GetData(counterData);
            int outCount = (int)counterData[0];

            if (verbose) Debug.Log($"{logPrefix} in={n} out={outCount} radius={neighborRadius:F3} min={minNeighbors} max={(maxNeighbors<0? -1 : maxNeighbors)}");

            if (outCount <= 0){
                // No valid points; drop this frame silently
                pointsOut.Dispose();
                return;
            }

            // Forward filtered splat
            var outSplat = new Splat(pointsOut, outCount, inSplat.JobId);
            // pointsOut ownership transferred; prevent dispose below
            pointsOut = null;
            base.InvokeReady(outSplat);
        }
        finally {
            if (cellHead != null) cellHead.Dispose();
            if (nextIndex != null) nextIndex.Dispose();
            if (maskBuf != null) maskBuf.Dispose();
            if (counter != null) counter.Dispose();
            if (pointsOut != null) pointsOut.Dispose();
        }
    }
}


