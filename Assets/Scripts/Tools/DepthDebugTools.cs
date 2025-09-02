using UnityEngine;

namespace ImOTAR.Tools
{
    public static class DepthDebugTools
    {
        public static bool Enabled = true;

        public static void LogRenderTexture(RenderTexture rt, string tag = "DepthDebug", bool printSamples = false)
        {
            if (!Enabled || rt == null){
                Debug.Log("cancelled");
                return;
            }
            Debug.Log($"[{tag}] Inspecting RenderTexture: {rt.width}x{rt.height} format={rt.format}");
            DepthRTInspector.DumpStats(rt, printSamples);
        }
    }
}


