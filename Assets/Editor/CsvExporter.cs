// Unity 2020.1 以降 / Unity 6.x で動作確認想定
// 使い方: メニュー Tools/Profiler/Export .data to per-category CSVs
// 1) .data を選択 → 2) 出力フォルダを選択 → 書き出し

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.Profiling;
using UnityEngine;
using Unity.Profiling.LowLevel.Unsafe; // ProfilerUnsafeUtility

public static class ProfilerDataExporter
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    [MenuItem("Tools/Profiler/Export .data to per-category CSVs")]
    private static void Export()
    {
        var dataPath = EditorUtility.OpenFilePanel("Select Unity Profiler .data", "", "data");
        if (string.IsNullOrEmpty(dataPath)) return;

        var outDir = EditorUtility.OpenFolderPanel("Select output folder", "", "");
        if (string.IsNullOrEmpty(outDir)) return;

        try
        {
            // .data をロード
            ProfilerDriver.LoadProfile(dataPath, keepExistingData: false); // :contentReference[oaicite:2]{index=2}

            var first = ProfilerDriver.firstFrameIndex;
            var last  = ProfilerDriver.lastFrameIndex;                      // :contentReference[oaicite:3]{index=3}
            if (first < 0 || last < 0 || last < first)
                throw new InvalidOperationException("Profiler frames not available in the loaded .data.");

            // カテゴリごとに CSV ライターを用意
            var writers = new Dictionary<ushort, StreamWriter>();
            string MakePath(string name) => Path.Combine(outDir, SanitizeFileName(name) + ".csv");

            // 追加でフレームサマリ/スレッドサマリも出力
            using var framesCsv  = new StreamWriter(MakePath("Frames"), false, Encoding.UTF8);
            framesCsv.WriteLine("frameIndex,cpuFrameTimeMs,gpuFrameTimeMs,fps"); // RawFrameDataView の frameTimeMs 等 :contentReference[oaicite:4]{index=4}

            using var threadsCsv = new StreamWriter(MakePath("Threads"), false, Encoding.UTF8);
            threadsCsv.WriteLine("frameIndex,threadIndex,threadGroup,threadName,sampleCount");

            // カテゴリID → 名前の簡易マップ（ProfilerUnsafeUtility の定数に対応） :contentReference[oaicite:5]{index=5}
            static string CategoryName(ushort cat) => cat switch
            {
                ProfilerUnsafeUtility.CategoryRender   => "Rendering",
                ProfilerUnsafeUtility.CategoryScripts  => "Scripts",
                ProfilerUnsafeUtility.CategoryPhysics  => "Physics",
                ProfilerUnsafeUtility.CategoryAnimation=> "Animation",
                ProfilerUnsafeUtility.CategoryAudio    => "Audio",
                ProfilerUnsafeUtility.CategoryVideo    => "Video",
                ProfilerUnsafeUtility.CategoryParticles=> "Particles",
                ProfilerUnsafeUtility.CategoryNetwork  => "Network",
                ProfilerUnsafeUtility.CategoryLoading  => "Loading",
                ProfilerUnsafeUtility.CategoryOther    => "Other",
                _ => $"Category_{cat}"
            };

            // 全フレームを走査
            for (int frame = first; ; )
            {
                // 各スレッドを列挙（0 から順に、invalid で打ち切り） :contentReference[oaicite:6]{index=6}
                int totalSamplesThisFrame = 0;
                double cpuFrameMs = 0, gpuFrameMs = 0, fps = 0;

                for (int threadIdx = 0; ; ++threadIdx)
                {
                    using var rv = ProfilerDriver.GetRawFrameDataView(frame, threadIdx); // RawFrameDataView 取得 :contentReference[oaicite:7]{index=7}
                    if (rv == null || !rv.valid) break;

                    // フレームの基本統計（CPU/GPU/FPS） :contentReference[oaicite:8]{index=8}
                    cpuFrameMs = rv.frameTimeMs;
                    gpuFrameMs = rv.frameGpuTimeMs;
                    fps        = rv.frameFps;

                    int sampleCount = rv.sampleCount;
                    totalSamplesThisFrame += sampleCount;

                    threadsCsv.WriteLine(string.Join(",",
                        frame.ToString(Ci),
                        threadIdx.ToString(Ci),
                        Csv(rv.threadGroupName),
                        Csv(rv.threadName),
                        sampleCount.ToString(Ci)));

                    // 各サンプルを列挙
                    for (int i = 0; i < sampleCount; i++)
                    {
                        // カテゴリIDを取得（ProfilerUnsafeUtility.* に対応） :contentReference[oaicite:9]{index=9}
                        ushort cat = rv.GetSampleCategoryIndex(i);

                        if (!writers.TryGetValue(cat, out var w))
                        {
                            var path = MakePath($"Samples_{CategoryName(cat)}");
                            w = new StreamWriter(path, false, Encoding.UTF8);
                            writers[cat] = w;
                            // 各サンプル CSV のヘッダ
                            w.WriteLine("frameIndex,threadIndex,categoryId,categoryName,markerId,markerName,startTimeNs,durationNs,startTimeMs,durationMs");
                        }

                        int markerId = rv.GetSampleMarkerId(i);                      // :contentReference[oaicite:10]{index=10}
                        string marker = rv.GetMarkerName(markerId);                  // :contentReference[oaicite:11]{index=11}
                        ulong startNs = rv.GetSampleStartTimeNs(i);                  // :contentReference[oaicite:12]{index=12}
                        ulong durNs   = rv.GetSampleTimeNs(i);                       // :contentReference[oaicite:13]{index=13}
                        double startMs= rv.GetSampleStartTimeMs(i);
                        float  durMs  = rv.GetSampleTimeMs(i);

                        w.WriteLine(string.Join(",",
                            frame.ToString(Ci),
                            threadIdx.ToString(Ci),
                            cat.ToString(Ci),
                            Csv(CategoryName(cat)),
                            markerId.ToString(Ci),
                            Csv(marker),
                            startNs.ToString(Ci),
                            durNs.ToString(Ci),
                            startMs.ToString(Ci),
                            durMs.ToString(Ci)));
                    }
                }

                framesCsv.WriteLine(string.Join(",",
                    frame.ToString(Ci),
                    cpuFrameMs.ToString(Ci),
                    gpuFrameMs.ToString(Ci),
                    fps.ToString(Ci)));

                if (frame == last) break;
                frame = ProfilerDriver.GetNextFrameIndex(frame); // 次フレームへ :contentReference[oaicite:14]{index=14}
                if (frame < 0) break;
            }

            foreach (var w in writers.Values) w.Dispose();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Export completed", "CSV export finished.", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Export failed", ex.Message, "OK");
        }
    }

    private static string Csv(string s)
    {
        if (s == null) return "";
        s = s.Replace("\"", "\"\"");
        return $"\"{s}\"";
    }

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
