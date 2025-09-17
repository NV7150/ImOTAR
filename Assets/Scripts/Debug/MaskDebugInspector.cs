using UnityEngine;

/// <summary>
/// FrameProviderが更新されるたびにMaskRTInspectorでサマリーをログ出力するデバッグ用コンポーネント
/// RInt (R32_SInt) マスクテクスチャ専用
/// </summary>
public class MaskDebugInspector : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private FrameProvider targetProvider;
    
    [Header("Settings")]
    [SerializeField] private bool logOnUpdate = true;
    [SerializeField] private bool printSamples = false;

    void Start()
    {
        if (targetProvider != null)
        {
            targetProvider.OnFrameUpdated += OnFrameUpdated;
        }
        else
        {
            Debug.LogWarning($"[{name}] targetProvider が設定されていません");
        }
    }

    void OnDestroy()
    {
        if (targetProvider != null)
        {
            targetProvider.OnFrameUpdated -= OnFrameUpdated;
        }
    }

    private void OnFrameUpdated(RenderTexture frameTexture)
    {
        if (!logOnUpdate || frameTexture == null) return;

        Debug.Log($"[MaskDebugInspector] {targetProvider.name} フレーム更新:");
        MaskRTInspector.DumpStats(frameTexture, printSamples);
    }

    // Inspector用のテストボタン
    [ContextMenu("Inspect Current Frame")]
    void InspectCurrentFrame()
    {
        if (targetProvider?.FrameTex != null)
        {
            Debug.Log($"[MaskDebugInspector] {targetProvider.name} 現在のフレーム:");
            MaskRTInspector.DumpStats(targetProvider.FrameTex, printSamples);
        }
        else
        {
            Debug.LogWarning("targetProvider または FrameTex が null です");
        }
    }
}
