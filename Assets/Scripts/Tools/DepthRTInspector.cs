using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

public static class DepthRTInspector
{
    // RFloat 推奨。RHalf(R16_SFloat)のときは後注を参照
    public static void DumpStats(RenderTexture rt, bool printSamples = true)
    {
        var req = AsyncGPUReadback.Request(rt, 0);
        req.WaitForCompletion();
        if (req.hasError) { Debug.LogError("AsyncGPUReadback failed."); return; }

        // RFloat なら 1ch = float として受け取れる
        var data = req.GetData<float>();
        int w = rt.width, h = rt.height, n = w * h;

        float min = float.PositiveInfinity, max = float.NegativeInfinity, sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float v = data[i];
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        float mean = sum / n;
        Debug.Log($"[DepthRTInspector] {w}x{h}  min={min}, max={max}, mean={mean}");

        if (printSamples)
        {
            int[] xs = { 0, w / 4, w / 2, 3 * w / 4, w - 1 };
            int[] ys = { 0, h / 4, h / 2, 3 * h / 4, h - 1 };
            foreach (var y in ys)
                foreach (var x in xs)
                    Debug.Log($"({x},{y}) = {data[y * w + x]}");
        }
    }
}
