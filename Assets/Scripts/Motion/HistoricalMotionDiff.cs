using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class HistoricalMotionDiff : PoseDiffManager {
    [Header("Inputs")]
    [SerializeField] private AsyncFrameProvider provider;  // Async job lifecycle
    [SerializeField] private MotionObtain motion;          // Absolute pose source

    [Header("Reference Freshness")]
    [SerializeField, Min(1f)] private float maxReferenceAgeMs = 200f;

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
        // Prefer ReferencePoseData if fresh enough; otherwise fall back to absolute pose
        Quaternion baseRot = Quaternion.identity;
        Vector3 basePos = Vector3.zero;

        var now = DateTime.UtcNow;
        bool tookReference = false;
        if (motion.TryGetLatestData<ReferencePoseData>(out var refPose)){
            float ageMs = (float)(now - refPose.Timestamp).TotalMilliseconds;
            if (ageMs <= maxReferenceAgeMs && refPose.IsStable){
                baseRot = refPose.Rotation;
                basePos = refPose.Position;
                tookReference = true;
            } else if (logVerbose){
                Debug.LogWarning($"{logPrefix} ReferencePose stale or unstable (ageMs={ageMs:F1}) at job start: {jobId}");
            }
        }

        if (!tookReference){
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
            baseRot = r.Rotation;
            basePos = p.Position;
        }

        _history[jobId] = new Snapshot(now, baseRot, basePos);
        _latestGen = jobId;
        _latestBaselineTs = now;
        if (logVerbose) Debug.Log($"{logPrefix} Capture baseline gen={_latestGen} ts={_latestBaselineTs:O} (ref={tookReference})");
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


