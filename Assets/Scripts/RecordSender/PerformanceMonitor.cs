using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace dang0.ServerLog{
    public enum MonitorStatus {
        UNSTARTED,
        RECORDING,
        RECORD_END,
        DATA_SENT,
        DATA_SENT_FAILED
    }

    [DisallowMultipleComponent]
    public sealed class PerformanceMonitor : MonoBehaviour {
        private const string DATE_FORMAT = "yyyy-MM-dd_HH:mm:ss";
        private const string FILE_NAME_FORMAT = "performance_{0}.json";

        [SerializeField] private float interval = 1.0f;
        [SerializeField] private bool enableCpuFrameTime = true;
        [SerializeField] private bool enableCpuMainThreadFrameTime = true;
        [SerializeField] private bool enableGpuFrameTime = true;
        [SerializeField] private bool enableTotalAllocatedMemory = true;
        [SerializeField] private bool enableTotalReservedMemory = false;
        [SerializeField] private bool enableTotalUnusedReservedMemory = false;
        [SerializeField] private bool enableMonoUsedSize = true;
        [SerializeField] private bool enableBatteryLevel = true;
        [SerializeField] private bool enableBatteryStatus = true;
        [SerializeField] private string subjectId;
        [SerializeField] private Sender sender;

        private MonitorStatus status = MonitorStatus.UNSTARTED;
        private List<MetricData> recordedData = new List<MetricData>();
        private Coroutine recordingCoroutine;

        public string ExperimentId { get; set; }
        public MonitorStatus Status => status;

        public void StartMonitor(){
            if (status == MonitorStatus.RECORDING) throw new InvalidOperationException("Monitor is already recording");
            if (string.IsNullOrEmpty(subjectId)) throw new InvalidOperationException("subjectId is not set");
            if (string.IsNullOrEmpty(ExperimentId)) throw new InvalidOperationException("ExperimentId is not set");
            if (sender == null) throw new NullReferenceException("Sender is not assigned");

            status = MonitorStatus.RECORDING;
            recordedData.Clear();
            recordingCoroutine = StartCoroutine(RecordMetrics());
        }

        public void EndMonitor(){
            if (status != MonitorStatus.RECORDING) throw new InvalidOperationException("Monitor is not recording");

            if (recordingCoroutine != null){
                StopCoroutine(recordingCoroutine);
                recordingCoroutine = null;
            }

            status = MonitorStatus.RECORD_END;

            try {
                var jsonArray = ConvertToJsonArray(recordedData);
                var timestamp = DateTime.Now.ToString(DATE_FORMAT);
                var fileName = string.Format(FILE_NAME_FORMAT, timestamp);
                var filePath = Path.Combine(Application.persistentDataPath, fileName);
                
                File.WriteAllText(filePath, jsonArray, Encoding.UTF8);
                Debug.Log($"Performance data saved to: {filePath}");

                var payload = new Payload(DateTime.Now, subjectId, ExperimentId, jsonArray);
                sender.Send(payload);
                status = MonitorStatus.DATA_SENT;
            } catch (Exception ex){
                Debug.LogError($"Failed to save or send performance data: {ex.Message}");
                status = MonitorStatus.DATA_SENT_FAILED;
                throw;
            }
        }

        private void Update(){
            if (status == MonitorStatus.RECORDING){
                FrameTimingManager.CaptureFrameTimings();
            }
        }

        private IEnumerator RecordMetrics(){
            while (status == MonitorStatus.RECORDING){
                yield return new WaitForSeconds(interval);
                CollectMetrics();
            }
        }

        private void CollectMetrics(){
            var timestamp = DateTime.Now.ToString(DATE_FORMAT);
            var data = new MetricData(timestamp);

            var timings = new FrameTiming[1];
            var capturedCount = FrameTimingManager.GetLatestTimings(1, timings);
            if (capturedCount > 0){
                var timing = timings[0];
                if (enableCpuFrameTime) data.SetCpuFrameTime(timing.cpuFrameTime);
                if (enableCpuMainThreadFrameTime) data.SetCpuMainThreadFrameTime(timing.cpuMainThreadFrameTime);
                if (enableGpuFrameTime) data.SetGpuFrameTime(timing.gpuFrameTime);
            }

            if (enableTotalAllocatedMemory) data.SetTotalAllocatedMemory(Profiler.GetTotalAllocatedMemoryLong());
            if (enableTotalReservedMemory) data.SetTotalReservedMemory(Profiler.GetTotalReservedMemoryLong());
            if (enableTotalUnusedReservedMemory) data.SetTotalUnusedReservedMemory(Profiler.GetTotalUnusedReservedMemoryLong());
            if (enableMonoUsedSize) data.SetMonoUsedSize(Profiler.GetMonoUsedSizeLong());

            if (enableBatteryLevel){
                var batteryLevel = SystemInfo.batteryLevel;
                data.SetBatteryLevel(batteryLevel >= 0 ? batteryLevel : null);
            }
            if (enableBatteryStatus){
                var batteryStatus = SystemInfo.batteryStatus;
                data.SetBatteryStatus(batteryStatus != BatteryStatus.Unknown ? batteryStatus.ToString() : null);
            }

            recordedData.Add(data);
        }

        private string ConvertToJsonArray(List<MetricData> dataList){
            var sb = new StringBuilder();
            sb.Append("{\"records\":[");
            if (dataList != null && dataList.Count > 0){
                for (int i = 0; i < dataList.Count; i++){
                    if (i > 0) sb.Append(",");
                    sb.Append(dataList[i].ToJson());
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
