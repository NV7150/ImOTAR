// ProfilerTimeEmbedder.cs
// ・毎フレーム（または interval 秒ごと）に Profiler に値を埋め込みます。
// ・必要: com.unity.profiling.core をインストール済み
// ・Profiler の Modules → Profiler Module Editor で "TimeSync (wall_unix_ns)" と "TimeSync (mono_ns)" を追加してください
//
// 使い方:
//  - GameObject にアタッチして再生
//  - intervalSeconds を 0 にすると毎フレーム、正の値で間引き

using System;
using System.Globalization;
using UnityEngine;
using Unity.Profiling;                // ProfilerMarker / ProfilerCounter
using Unity.Profiling.LowLevel;       // ProfilerCounterOptions (必要に応じ)
using Unity.Profiling.LowLevel.Unsafe; // ProfilerMarkerDataUnit (環境によっては省略可)

[DisallowMultipleComponent]
public class TimeSyncProfile : MonoBehaviour
{
    [Tooltip("0 = every frame. >0 = seconds between samples.")]
    public float intervalSeconds = 0f;

    [Tooltip("表示名プレフィックス")]
    public string modulePrefix = "TimeSync";

    // Profiler カウンタ / マーカー
    // カウンタは ProfilerCounter<T> / ProfilerCounterValue<T> のどちらかで利用可能
    private static ProfilerCounter<long> s_wallCounter;
    private static ProfilerCounter<long> s_monoCounter;

    float _acc;

    void Awake()
    {
        // ProfilerCategory.Scripts を使う例。必要なら ProfilerCategory.Other 等に変更可
        // ProfilerMarkerDataUnit は環境で列挙値名が違う場合があるため Count を無難に使う（表示単位はあとで気にしなくて良い）
        s_wallCounter = new ProfilerCounter<long>(ProfilerCategory.Scripts, $"{modulePrefix} (wall_unix_ns)", ProfilerMarkerDataUnit.Count);
        s_monoCounter = new ProfilerCounter<long>(ProfilerCategory.Scripts, $"{modulePrefix} (mono_ns)",   ProfilerMarkerDataUnit.Count);
    }

    void Update()
    {
        if (intervalSeconds > 0f)
        {
            _acc += Time.unscaledDeltaTime;
            if (_acc < intervalSeconds) return;
            _acc = 0f;
        }

        // 壁時計（DateTimeOffset.Now）を Unix ナノ秒で取得
        var now = DateTimeOffset.Now;
        long unixNs = (now.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) * 100L; // 1 tick = 100 ns

        // mono（単調）をナノ秒に
        double monoS = Time.realtimeSinceStartupAsDouble;
        long monoNs = (long)(monoS * 1_000_000_000.0);

        // サンプル（ProfilerCounter.Sample）
        s_wallCounter.Sample(unixNs);
        s_monoCounter.Sample(monoNs);

        // 参考: ProfilerMarker<T> をメタデータ的に使う例（任意）
        // var marker = new ProfilerMarker<long>($"{modulePrefix}-meta", "wall_unix_ns");
        // using (marker.Auto(unixNs)) { /* empty */ }
    }
}
