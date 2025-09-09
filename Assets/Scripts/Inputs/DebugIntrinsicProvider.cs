using UnityEngine;

[DisallowMultipleComponent]
public class DebugIntrinsicProvider : IntrinsicProviderBase {
    [Header("Manual Intrinsics (Pixels)")]
    [SerializeField] private float fxPx = 1000f;
    [SerializeField] private float fyPx = 1000f;
    [SerializeField] private float cxPx = 320f;
    [SerializeField] private float cyPx = 240f;
    [SerializeField] private int width = 640;
    [SerializeField] private int height = 480;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;
    [SerializeField] private string logPrefix = "[DebugIntrinsicProvider]";

    public override IntrinsicParam GetIntrinsics() {
        bool valid = width > 0 && height > 0 && fxPx > 0f && fyPx > 0f;
        return new IntrinsicParam(fxPx, fyPx, cxPx, cyPx, width, height, valid);
    }

    private void OnValidate(){
        var intrinsics = GetIntrinsics();
        if (verboseLogging && intrinsics.isValid){
            Debug.Log($"{logPrefix} Intrinsics: fx={intrinsics.fxPx:F2} fy={intrinsics.fyPx:F2} cx={intrinsics.cxPx:F2} cy={intrinsics.cyPx:F2} res={intrinsics.width}x{intrinsics.height}");
        }
    }

    [ContextMenu("Log Current Intrinsics")]
    private void LogIntrinsics(){
        var intrinsics = GetIntrinsics();
        if (intrinsics.isValid){
            Debug.Log($"{logPrefix} Current: fx={intrinsics.fxPx:F2} fy={intrinsics.fyPx:F2} cx={intrinsics.cxPx:F2} cy={intrinsics.cyPx:F2} res={intrinsics.width}x{intrinsics.height}");
            Debug.Log($"{logPrefix} Normalized: fx={intrinsics.FxN:F6} fy={intrinsics.FyN:F6} cx={intrinsics.CxN:F6} cy={intrinsics.CyN:F6}");
        } else {
            Debug.LogWarning($"{logPrefix} Intrinsics not valid");
        }
    }
}
