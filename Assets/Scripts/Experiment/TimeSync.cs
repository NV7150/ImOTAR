// Assets/Scripts/MetalHUDTimeAnchor.cs
// 起動時に一度だけ壁時計と単調時刻の対応を出力（"metal-HUD" を含む）
using System;
using System.Globalization;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class TimeSync : MonoBehaviour
{
    [Tooltip("実行開始時に1行だけ出力します")]
    public bool logOnStart = true;

    [Tooltip("任意のラン識別子（解析時の突合せ用）")]
    public string runTag = "";

    const string Tag = "metal-HUD"; // 抽出フィルタ用

    void Start()
    {
        if (!logOnStart) return;

        var now = DateTimeOffset.Now; // 現在の壁時計
        string wall = $"{now:HH':'mm':'ss}.{now:ffffff}{OffsetHHmm(now.Offset)}";

        long unixNs = (now - DateTimeOffset.UnixEpoch).Ticks * 100L; // 1 tick = 100ns
        double monoSec = Time.realtimeSinceStartupAsDouble;          // 単調時刻（起動からの秒）

        Debug.Log(
            $"[{Tag}] wall={wall} unix_ns={unixNs} mono_s={monoSec.ToString("F6", CultureInfo.InvariantCulture)} frame={Time.frameCount} tag={runTag}"
        );
    }

    static string OffsetHHmm(TimeSpan off)
    {
        var sign = off.Ticks >= 0 ? "+" : "";
        var a = off.Duration();
        return sign + a.Hours.ToString("00") + a.Minutes.ToString("00");
    }
}
