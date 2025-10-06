using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class HistoricalMotionDiff : PoseDiffManager {
    [Header("Inputs")]
    [SerializeField] private AsyncFrameProvider provider;  // Async job lifecycle
    [SerializeField] private MotionObtain motion;          // Absolute pose source

    [Header("Debug")]
    [SerializeField] private bool logVerbose = false;
    [SerializeField] private string logPrefix = "[HistoricalMotionDiff]";

    private struct Snapshot {
        public readonly DateTime Timestamp;
        public readonly Quaternion BaseRotation;
        public readonly Vector3 BasePosition;

        public Snapshot(DateTime ts, Quaternion rot, Vector3 pos){
            Timestamp = ts;
            BaseRotation = rot;
            BasePosition = pos;
        }
    }

    private readonly Dictionary<Guid, Snapshot> _history = new Dictionary<Guid, Snapshot>();
    private Guid _latestGen = Guid.Empty;
    private DateTime _latestBaselineTs = DateTime.MinValue;


    private void OnEnable(){
        if (provider == null) throw new NullReferenceException("HistoricalMotionDiff: provider not assigned");
        if (motion == null) throw new NullReferenceException("HistoricalMotionDiff: motion not assigned");
        provider.OnAsyncFrameStarted += OnJobStarted;
        provider.OnAsyncFrameCanceled += OnJobCanceled;
    }

    private void OnDisable(){
        if (provider != null){
            provider.OnAsyncFrameStarted -= OnJobStarted;
            provider.OnAsyncFrameCanceled -= OnJobCanceled;
        }
    }

    private void OnJobStarted(Guid jobId){
        // Capture snapshot for this generation if data available
        if (!motion.TryGetLatestData<AbsoluteRotationData>(out var r)){
            if (logVerbose) 
                Debug.LogWarning($"{logPrefix} Rotation unavailable at job start: {jobId}");
            return;
        }
        if (!motion.TryGetLatestData<AbsolutePositionData>(out var p)){
            if (logVerbose) 
                Debug.LogWarning($"{logPrefix} Position unavailable at job start: {jobId}");
            return;
        }

        var ts = DateTime.UtcNow;
        _history[jobId] = new Snapshot(ts, r.Rotation, p.Position);
        _latestGen = jobId;
        _latestBaselineTs = ts;
        if (logVerbose) Debug.Log($"{logPrefix} Capture baseline gen={_latestGen} ts={_latestBaselineTs:O}");
    }

    private void OnJobCanceled(Guid jobId){
        // Always keep history as requested
        if (logVerbose) 
            Debug.Log($"{logPrefix} Job canceled gen={jobId} (kept in history)");
    }

    public override bool TryGetDiffFrom(Guid generation, out Vector3 pos, out Quaternion rot){
        pos = Vector3.zero;
        rot = Quaternion.identity;

        if (generation == Guid.Empty) return false;
        if (!_history.TryGetValue(generation, out var snap)) return false;

        if (!motion.TryGetLatestData<AbsoluteRotationData>(out var currR)){
            if (logVerbose) Debug.LogWarning($"{logPrefix} Rotation unavailable in TryGetDiffFrom for gen={generation}");
            return false;
        }
        if (!motion.TryGetLatestData<AbsolutePositionData>(out var currP)){
            if (logVerbose) Debug.LogWarning($"{logPrefix} Position unavailable in TryGetDiffFrom for gen={generation}");
            return false;
        }

        rot = Quaternion.Inverse(currR.Rotation) * snap.BaseRotation;
        pos = Quaternion.Inverse(currR.Rotation) * (snap.BasePosition - currP.Position);
        return true;
    }
}


