using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

public static class MaskRTInspector
{
    // RInt (R32_SInt) 専用。マスク値の統計とサンプルを表示
    public static void DumpStats(RenderTexture rt, bool printSamples = true)
    {
        var req = AsyncGPUReadback.Request(rt, 0);
        req.WaitForCompletion();
        if (req.hasError) { Debug.LogError("AsyncGPUReadback failed."); return; }

        // RInt なら int として受け取る
        var data = req.GetData<int>();
        int w = rt.width, h = rt.height, n = w * h;

        int min = int.MaxValue, max = int.MinValue;
        int sum = 0;
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        
        for (int i = 0; i < n; i++)
        {
            int v = data[i];
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
            
            if (counts.ContainsKey(v))
                counts[v]++;
            else
                counts[v] = 1;
        }
        
        float mean = (float)sum / n;
        Debug.Log($"[MaskRTInspector] {w}x{h}  min={min}, max={max}, mean={mean:F2}");
        
        // 各値の出現回数を表示
        var sortedCounts = counts.OrderBy(kvp => kvp.Key);
        foreach (var kvp in sortedCounts)
        {
            float percentage = (float)kvp.Value / n * 100f;
            Debug.Log($"  Value {kvp.Key}: {kvp.Value} pixels ({percentage:F1}%)");
        }

        if (printSamples)
        {
            int[] xs = { 0, w / 4, w / 2, 3 * w / 4, w - 1 };
            int[] ys = { 0, h / 4, h / 2, 3 * h / 4, h - 1 };
            foreach (var y in ys)
                foreach (var x in xs)
                    Debug.Log($"  ({x},{y}) = {data[y * w + x]}");
        }
    }
}
