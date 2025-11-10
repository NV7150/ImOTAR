// Assets/Editor/ProfilerFramesMonoExporter.cs
// Unity 2021.3+ / 2022.x / 6000.x（Unity 6）想定
// メニュー: Tools/Profiler/Export Frames CSV (mono_s + CPU/FPS/Memory)
// 出力: Frames_Mono_CPU_FPS_Memory.csv
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.Profiling;

public static class ProfilerFramesMonoExporter
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    [MenuItem("Tools/Profiler/Export Frames CSV Sync")]
    private static void Export()
    {
        var dataPath = EditorUtility.OpenFilePanel("Select Unity Profiler .data", "", "data");
        if (string.IsNullOrEmpty(dataPath)) return;

        var outDir = EditorUtility.OpenFolderPanel("Select output folder", "", "");
        if (string.IsNullOrEmpty(outDir)) return;

        try
        {
            // .data をロード
            ProfilerDriver.LoadProfile(dataPath, keepExistingData: false);

            int first = ProfilerDriver.firstFrameIndex;
            int last  = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < 0 || last < first)
                throw new InvalidOperationException("No valid frames in the loaded .data.");

            var outPath = Path.Combine(outDir, "Frames_Mono_CPU_FPS_Memory.csv");
            using var sw = new StreamWriter(outPath, false, Encoding.UTF8);

            // ヘッダ（mono_s を主タイムスタンプとして出力）
            // 末尾に TimeSync カウンタを追加（存在しないフレームは -1）
            sw.WriteLine("frameIndex,mono_s,mono_ms,cpuFrameTimeMs,fps,usedTotalMemoryMB,totalReservedMemoryMB,gcUsedMemoryMB,systemUsedMemoryMB,wall_unix_ns,mono_ns");

            // メモリカウンタ名（存在しない環境では空欄になる）
            string[] memCounters = {
                "Used Total Memory",
                "Total Reserved Memory",
                "GC Used Memory",
                "System Used Memory"
            };
            int usedId=-1, reservId=-1, gcId=-1, sysId=-1;
            bool resolved = false;

            // TimeSync カウンタ（固定名）
            int wallId = -1, monoId = -1;
            bool timeResolved = false;

            for (int frame = first; ; )
            {
                RawFrameDataView rv = null;
                // 最初の有効 RawFrameDataView を取得（スレッド 0 から順に）
                int t = 0;
                while (true)
                {
                    var tmp = ProfilerDriver.GetRawFrameDataView(frame, t);
                    if (tmp == null || !tmp.valid)
                    {
                        tmp?.Dispose();
                        break; // 取得終了（rv が null のままならこのフレームは空）
                    }
                    rv = tmp; // 最初の有効ビューのみ使用
                    break;
                }

                if (rv == null)
                {
                    // RawFrameDataView が取得できないフレーム: 値は空欄、TimeSync は -1
                    sw.WriteLine(string.Join(",",
                        frame.ToString(Ci),
                        "",
                        "",
                        "",
                        "",
                        "",
                        "",
                        "",
                        "",
                        (-1).ToString(Ci),
                        (-1).ToString(Ci)));
                    if (frame == last) break;
                    frame = ProfilerDriver.GetNextFrameIndex(frame);
                    continue;
                }

                // 単調タイムスタンプ（フレーム開始時刻）
                double mono_ms = rv.frameStartTimeMs;     // ms（単調）
                double mono_s  = mono_ms / 1000.0;        // s（単調）

                // フレーム統計
                double cpuMs = rv.frameTimeMs;            // CPU frame time (ms)
                double fps   = rv.frameFps;               // FPS

                // メモリカウンタの解決（最初のフレームで一度だけ）
                if (!resolved)
                {
                    usedId   = rv.GetMarkerId(memCounters[0]);
                    reservId = rv.GetMarkerId(memCounters[1]);
                    gcId     = rv.GetMarkerId(memCounters[2]);
                    sysId    = rv.GetMarkerId(memCounters[3]);
                    resolved = true;
                }

                // TimeSync カウンタの解決（最初のフレームで一度だけ）
                if (!timeResolved)
                {
                    wallId = rv.GetMarkerId("TimeSync (wall_unix_ns)");
                    monoId = rv.GetMarkerId("TimeSync (mono_ns)");
                    timeResolved = true;
                }

                // メモリ値（MB）。無い場合は空欄で出力
                string usedMB   = GetCounter(rv, usedId);
                string reservMB = GetCounter(rv, reservId);
                string gcMB     = GetCounter(rv, gcId);
                string sysMB    = GetCounter(rv, sysId);

                // TimeSync カウンタ（なければ -1）。double から丸めて long 文字列化（精度要件は ns 単位の整数）
                long wallOut = -1;
                long monoOut = -1;
                if (wallId >= 0 && rv.HasCounterValue(wallId))
                {
                    var v = rv.GetCounterValueAsDouble(wallId);
                    wallOut = (long)Math.Round(v);
                }
                if (monoId >= 0 && rv.HasCounterValue(monoId))
                {
                    var v = rv.GetCounterValueAsDouble(monoId);
                    monoOut = (long)Math.Round(v);
                }

                sw.WriteLine(string.Join(",",
                    frame.ToString(Ci),
                    mono_s.ToString(Ci),
                    mono_ms.ToString(Ci),
                    cpuMs.ToString(Ci),
                    fps.ToString(Ci),
                    usedMB, reservMB, gcMB, sysMB,
                    wallOut.ToString(Ci), monoOut.ToString(Ci)));

                rv.Dispose();

                if (frame == last) break;
                frame = ProfilerDriver.GetNextFrameIndex(frame);
                if (frame < 0) break;
            }

            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Export completed", $"Saved:\n{outPath}", "OK");
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("Export failed", ex.Message, "OK");
        }
    }

    private static string GetCounter(RawFrameDataView rv, int markerId)
    {
        if (markerId < 0) return "";
        try
        {
            if (rv.HasCounterValue(markerId))
            {
                double v = rv.GetCounterValueAsDouble(markerId);
                return v.ToString(Ci);
            }
        }
        catch { }
        return "";
    }
}
