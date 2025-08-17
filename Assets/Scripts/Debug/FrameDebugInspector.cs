using UnityEngine;

/// <summary>
/// FrameProviderが更新されるたびにDepthRTInspectorでサマリーをログ出力するデバッグ用コンポーネント
/// </summary>
public class FrameDebugInspector : MonoBehaviour
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

        Debug.Log($"[FrameDebugInspector] {targetProvider.name} フレーム更新:");
        DepthRTInspector.DumpStats(frameTexture, printSamples);
    }

    // Inspector用のテストボタン
    [ContextMenu("Inspect Current Frame")]
    void InspectCurrentFrame()
    {
        if (targetProvider?.FrameTex != null)
        {
            Debug.Log($"[FrameDebugInspector] {targetProvider.name} 現在のフレーム:");
            DepthRTInspector.DumpStats(targetProvider.FrameTex, printSamples);
        }
        else
        {
            Debug.LogWarning("targetProvider または FrameTex が null です");
        }
    }
}
