using UnityEngine;
using UnityEngine.Profiling;
using System;

public class ProfilerRawLogger : MonoBehaviour
{
    [SerializeField] private string logName = "";
    void Awake()
    {
        // iOS では書き込み可能なサンドボックス配下（Documents 等）に置く
        var path = System.IO.Path.Combine(Application.persistentDataPath, $"{logName}-{DateTime.Now.ToString("yyyyMMdd_HHmmss")}.raw");
        Profiler.logFile = path;           // 出力先を指定
        Profiler.enableBinaryLog = true;   // バイナリログを有効化（拡張子 .raw が自動付与）
        Profiler.enabled = true;           // プロファイラ自体を有効化

        // 取りこぼし回避（必要に応じて増やす）
        // Profiler.maxUsedMemory = 256 * 1024 * 1024;
    }

    void OnApplicationQuit()
    {
        Profiler.enableBinaryLog = false;
        Profiler.enabled = false;
        Profiler.logFile = null;
    }
}
