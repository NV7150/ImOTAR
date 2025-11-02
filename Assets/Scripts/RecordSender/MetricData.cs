using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace dang0.ServerLog{
    [Serializable]
    public class MetricData {
        [SerializeField] private string timestamp;
        [SerializeField] private double? cpuFrameTime;
        [SerializeField] private double? cpuMainThreadFrameTime;
        [SerializeField] private double? gpuFrameTime;
        [SerializeField] private long? totalAllocatedMemory;
        [SerializeField] private long? totalReservedMemory;
        [SerializeField] private long? totalUnusedReservedMemory;
        [SerializeField] private long? monoUsedSize;
        [SerializeField] private float? batteryLevel;
        [SerializeField] private string batteryStatus;

        public string Timestamp => timestamp;
        public double? CpuFrameTime => cpuFrameTime;
        public double? CpuMainThreadFrameTime => cpuMainThreadFrameTime;
        public double? GpuFrameTime => gpuFrameTime;
        public long? TotalAllocatedMemory => totalAllocatedMemory;
        public long? TotalReservedMemory => totalReservedMemory;
        public long? TotalUnusedReservedMemory => totalUnusedReservedMemory;
        public long? MonoUsedSize => monoUsedSize;
        public float? BatteryLevel => batteryLevel;
        public string BatteryStatus => batteryStatus;

        public MetricData(string timestamp){
            if (string.IsNullOrEmpty(timestamp)) throw new ArgumentException("timestamp is null or empty", nameof(timestamp));
            this.timestamp = timestamp;
        }

        public void SetCpuFrameTime(double? value) => cpuFrameTime = value;
        public void SetCpuMainThreadFrameTime(double? value) => cpuMainThreadFrameTime = value;
        public void SetGpuFrameTime(double? value) => gpuFrameTime = value;
        public void SetTotalAllocatedMemory(long? value) => totalAllocatedMemory = value;
        public void SetTotalReservedMemory(long? value) => totalReservedMemory = value;
        public void SetTotalUnusedReservedMemory(long? value) => totalUnusedReservedMemory = value;
        public void SetMonoUsedSize(long? value) => monoUsedSize = value;
        public void SetBatteryLevel(float? value) => batteryLevel = value;
        public void SetBatteryStatus(string value) => batteryStatus = value;

        public string ToJson(){
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"timestamp\":\"").Append(timestamp).Append('"');
            
            if (cpuFrameTime.HasValue){
                sb.Append(",\"cpuFrameTime\":").Append(cpuFrameTime.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (cpuMainThreadFrameTime.HasValue){
                sb.Append(",\"cpuMainThreadFrameTime\":").Append(cpuMainThreadFrameTime.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (gpuFrameTime.HasValue){
                sb.Append(",\"gpuFrameTime\":").Append(gpuFrameTime.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (totalAllocatedMemory.HasValue){
                sb.Append(",\"totalAllocatedMemory\":").Append(totalAllocatedMemory.Value);
            }
            if (totalReservedMemory.HasValue){
                sb.Append(",\"totalReservedMemory\":").Append(totalReservedMemory.Value);
            }
            if (totalUnusedReservedMemory.HasValue){
                sb.Append(",\"totalUnusedReservedMemory\":").Append(totalUnusedReservedMemory.Value);
            }
            if (monoUsedSize.HasValue){
                sb.Append(",\"monoUsedSize\":").Append(monoUsedSize.Value);
            }
            if (batteryLevel.HasValue){
                sb.Append(",\"batteryLevel\":").Append(batteryLevel.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(batteryStatus)){
                sb.Append(",\"batteryStatus\":\"").Append(batteryStatus).Append('"');
            }
            
            sb.Append('}');
            return sb.ToString();
        }
    }
}

