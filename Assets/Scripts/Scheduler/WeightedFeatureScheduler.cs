using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class WeightedFeatureScheduler : SchedulerBase {
    [Header("Inputs")]
    [SerializeField] private MotionObtain motionSource;
    [SerializeField] private List<MotionFeatureExtractorBase> extractors = new List<MotionFeatureExtractorBase>();
    [SerializeField] private List<float> weights = new List<float>();

    [Header("Thresholds (two-boundary)")]
    [SerializeField] private float stopThreshold = 0.01f;   // score < stop => STOP
    [SerializeField] private float highSpeedThreshold = 0.5f; // score >= high => HIGH_SPEED; otherwise LOW_SPEED

    [Header("Debug")]
    [SerializeField] private bool verboseLogs = false;

    private NativeArray<float> _weightsNative;
    private NativeArray<float> _featuresNative;
    private NativeArray<float> _resultNative; // length = 1

    private ScheduleStatus _state = ScheduleStatus.STOP;
    public override ScheduleStatus CurrentState => _state;

    // Expose current concatenated features and weights for debug/UI
    public NativeArray<float> CurrentFeatures => _featuresNative;
    public NativeArray<float> CurrentWeights => _weightsNative;
    public int FeatureCount => _featuresNative.IsCreated ? _featuresNative.Length : 0;

    private void OnEnable(){
        if (motionSource == null) throw new System.NullReferenceException("WeightedFeatureScheduler: motionSource is not assigned");
        if (extractors == null || extractors.Count == 0) throw new System.NullReferenceException("WeightedFeatureScheduler: extractors is empty or null");
        for (int i = 0; i < extractors.Count; i++) if (extractors[i] == null) throw new System.NullReferenceException($"WeightedFeatureScheduler: extractor[{i}] is null");
        RebuildWeightsNative();
        if (!_resultNative.IsCreated) _resultNative = new NativeArray<float>(1, Allocator.Persistent);
    }

    private void OnDisable(){
        if (_weightsNative.IsCreated) _weightsNative.Dispose();
        if (_featuresNative.IsCreated) _featuresNative.Dispose();
        if (_resultNative.IsCreated) _resultNative.Dispose();
    }

    private void OnValidate(){
        // Ensure thresholds are monotonic for predictable behavior
        if (highSpeedThreshold < stopThreshold) highSpeedThreshold = stopThreshold;
    }

    private void RebuildWeightsNative(){
        if (_weightsNative.IsCreated) _weightsNative.Dispose();
        if (weights == null) weights = new List<float>();
        _weightsNative = new NativeArray<float>(weights.Count, Allocator.Persistent);
        for (int i = 0; i < _weightsNative.Length; i++) _weightsNative[i] = weights[i];
    }

    private int ComputeTotalFeatureLength(){
        int total = 0;
        if (extractors == null) return 0;
        for (int i = 0; i < extractors.Count; i++){
            var ext = extractors[i];
            if (ext == null) continue;
            var arr = ext.ExtractFeature(motionSource);
            total += arr.IsCreated ? arr.Length : 0;
        }
        return total;
    }

    private void EnsureFeaturesBuffer(int length){
        if (length <= 0){
            if (_featuresNative.IsCreated) _featuresNative.Dispose();
            return;
        }
        if (!_featuresNative.IsCreated || _featuresNative.Length != length){
            if (_featuresNative.IsCreated) _featuresNative.Dispose();
            _featuresNative = new NativeArray<float>(length, Allocator.Persistent);
        }
    }

    private int FillFeatures(){
        int offset = 0;
        if (extractors == null) return 0;
        for (int i = 0; i < extractors.Count; i++){
            var ext = extractors[i];
            if (ext == null) continue;
            var arr = ext.ExtractFeature(motionSource);
            if (!arr.IsCreated || arr.Length == 0) continue;
            int n = arr.Length;
            for (int j = 0; j < n && (offset + j) < _featuresNative.Length; j++){
                _featuresNative[offset + j] = arr[j];
            }
            offset += n;
            if (offset >= _featuresNative.Length) break;
        }
        return math.min(offset, _featuresNative.Length);
    }

    [BurstCompile]
    private struct DotJob : IJob {
        [ReadOnly] public NativeArray<float> a;
        [ReadOnly] public NativeArray<float> b;
        public NativeArray<float> result; // length = 1
        public void Execute(){
            int n = math.min(a.Length, b.Length);
            if (n <= 0){ result[0] = 0f; return; }
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += a[i] * b[i];
            result[0] = sum / n; // average instead of sum
        }
    }

    private void Update(){
        int totalLen = ComputeTotalFeatureLength();
        if (totalLen <= 0) throw new System.InvalidOperationException("WeightedFeatureScheduler: total feature length is zero; check extractors");
        if (!_weightsNative.IsCreated || _weightsNative.Length != totalLen) throw new System.InvalidOperationException($"WeightedFeatureScheduler: weights length({_weightsNative.Length}) must equal total feature length({totalLen})");
        EnsureFeaturesBuffer(totalLen);
        int filled = FillFeatures();
        if (filled == 0) throw new System.InvalidOperationException("WeightedFeatureScheduler: failed to fill features");

        // If weight length mismatches, compute with min length
        var job = new DotJob{
            a = _featuresNative,
            b = _weightsNative,
            result = _resultNative
        };
        job.Run();
        float score = _resultNative[0];
        UpdateState(score);
        if (verboseLogs) Debug.Log($"[WeightedFeatureScheduler] score={score:F6}, features={filled}, weights={_weightsNative.Length}");
    }

    private void UpdateState(float score){
        if (score < stopThreshold) { _state = ScheduleStatus.STOP; return; }
        if (score >= highSpeedThreshold) { _state = ScheduleStatus.HIGH_SPEED; return; }
        _state = ScheduleStatus.LOW_SPEED;
    }
}


