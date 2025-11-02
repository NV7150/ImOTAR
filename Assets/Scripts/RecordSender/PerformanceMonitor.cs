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
    public sealed class PerformanceMonitor : ExpLogger {
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
        [SerializeField] private ExperimentPhaseManager phaseManager;
        [SerializeField] private Sender sender;

        private MonitorStatus status = MonitorStatus.UNSTARTED;
        private Dictionary<ExperimentPhase, List<MetricData>> recordedDataByPhase = new Dictionary<ExperimentPhase, List<MetricData>>();
        private Coroutine recordingCoroutine;
        private string subjectId;
        private string experimentId;

        public MonitorStatus Status => status;

        public override void StartLogging(string subjectId, string experimentId){
            if (status == MonitorStatus.RECORDING) throw new InvalidOperationException("Monitor is already recording");
            if (string.IsNullOrEmpty(subjectId)) throw new ArgumentException("subjectId is null or empty", nameof(subjectId));
            if (string.IsNullOrEmpty(experimentId)) throw new ArgumentException("experimentId is null or empty", nameof(experimentId));
            if (phaseManager == null) throw new NullReferenceException("PerformanceMonitor: phaseManager not assigned");
            if (sender == null) throw new NullReferenceException("PerformanceMonitor: sender not assigned");

            this.subjectId = subjectId;
            this.experimentId = experimentId;
            status = MonitorStatus.RECORDING;
            recordedDataByPhase.Clear();
            recordingCoroutine = StartCoroutine(RecordMetrics());
        }

        public override void SendPhase(ExperimentPhase phase){
            if (sender == null) throw new NullReferenceException("PerformanceMonitor: sender not assigned");
            if (string.IsNullOrEmpty(subjectId)) throw new InvalidOperationException("PerformanceMonitor: StartLogging has not been called");

            if (!recordedDataByPhase.ContainsKey(phase) || recordedDataByPhase[phase].Count == 0){
                return;
            }

            try {
                var jsonArray = ConvertToJsonArray(recordedDataByPhase[phase]);
                var timestamp = DateTime.Now.ToString(DATE_FORMAT);
                var fileName = string.Format(FILE_NAME_FORMAT, timestamp + "_" + phase.ToString());
                var filePath = Path.Combine(Application.persistentDataPath, fileName);
                
                File.WriteAllText(filePath, jsonArray, Encoding.UTF8);
                Debug.Log($"Performance data saved to: {filePath}");

                var phasedPayload = new PhasedPayload(phase, jsonArray);
                var payload = new Payload(DateTime.Now, subjectId, experimentId, phasedPayload.ToJson());
                sender.Send(payload);
                status = MonitorStatus.DATA_SENT;
            } catch (Exception ex){
                Debug.LogError($"Failed to save or send performance data: {ex.Message}");
                status = MonitorStatus.DATA_SENT_FAILED;
                throw;
            }
        }

        public override void SendAllPhases(){
            if (string.IsNullOrEmpty(subjectId)) throw new InvalidOperationException("PerformanceMonitor: StartLogging has not been called");

            if (status == MonitorStatus.RECORDING){
                if (recordingCoroutine != null){
                    StopCoroutine(recordingCoroutine);
                    recordingCoroutine = null;
                }
                status = MonitorStatus.RECORD_END;
            }

            foreach (var phase in recordedDataByPhase.Keys){
                SendPhase(phase);
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

            var currentPhase = phaseManager.CurrPhase;
            if (!recordedDataByPhase.ContainsKey(currentPhase)){
                recordedDataByPhase[currentPhase] = new List<MetricData>();
            }
            recordedDataByPhase[currentPhase].Add(data);
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
